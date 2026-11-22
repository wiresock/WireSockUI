//go:build windows

package main

// Controller for the pinned Mullvad win-split-tunnel ABI. The driver stays
// unmodified and signed; private WFP sublayer GUIDs are passed at initialization.
import (
	"encoding/binary"
	"errors"
	"fmt"
	"log"
	"net"
	"net/netip"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"sync"
	"time"
	"unsafe"

	"golang.org/x/sys/windows"
	"golang.org/x/sys/windows/svc"
	"golang.org/x/sys/windows/svc/mgr"
	"golang.zx2c4.com/wireguard/windows/tunnel/winipcfg"
)

const splitServiceName = "cronixWGSplitTunnel"

var splitLayerKeys = [2]windows.GUID{
	{Data1: 0x61b3f116, Data2: 0xa561, Data3: 0x459a, Data4: [8]byte{0x9b, 0x8a, 0xe1, 0x28, 0x79, 0xa5, 0x55, 0x10}},
	{Data1: 0x61b3f116, Data2: 0xa561, Data3: 0x459a, Data4: [8]byte{0x9b, 0x8a, 0xe1, 0x28, 0x79, 0xa5, 0x55, 0x11}},
}
var fwpuclnt = windows.NewLazySystemDLL("fwpuclnt.dll")
var fwOpen = fwpuclnt.NewProc("FwpmEngineOpen0")
var fwClose = fwpuclnt.NewProc("FwpmEngineClose0")
var fwAddLayer = fwpuclnt.NewProc("FwpmSubLayerAdd0")
var fwDeleteLayer = fwpuclnt.NewProc("FwpmSubLayerDeleteByKey0")

type splitSubLayer struct {
	Key               windows.GUID
	Name, Description *uint16
	Flags             uint32
	Provider          *windows.GUID
	DataSize          uint32
	Data              *byte
	Weight            uint16
}

type splitTunnel struct {
	handle        windows.Handle
	engine        uintptr
	service       *mgr.Service
	manager       *mgr.Mgr
	layers        int
	closeOnce     sync.Once
	eventMu       sync.Mutex
	eventStop     bool
	eventDone     chan struct{}
	failures      chan error
	started       bool
	borrowed      bool
	resetOnClose  bool
	lastAddresses [40]byte
	haveAddresses bool
}

func splitIOCTL(function uint32) uint32 {
	method := uint32(0)
	if function == 8 || function == 11 {
		method = 3
	}
	return 0x80000000 | function<<2 | method
}
func (s *splitTunnel) ioctl(function uint32, in, out []byte) error {
	var ip, op *byte
	if len(in) > 0 {
		ip = &in[0]
	}
	if len(out) > 0 {
		op = &out[0]
	}
	event, err := windows.CreateEvent(nil, 1, 0, nil)
	if err != nil {
		return err
	}
	defer windows.CloseHandle(event)
	over := windows.Overlapped{HEvent: event}
	var n uint32
	err = windows.DeviceIoControl(s.handle, splitIOCTL(function), ip, uint32(len(in)), op, uint32(len(out)), &n, &over)
	if errors.Is(err, windows.ERROR_IO_PENDING) {
		err = windows.GetOverlappedResult(s.handle, &over, &n, true)
	}
	if err != nil {
		return fmt.Errorf("split tunnel IOCTL %d: %w", function, err)
	}
	if len(out) > 0 && n != uint32(len(out)) {
		return fmt.Errorf("split tunnel IOCTL %d: unexpected reply length %d", function, n)
	}
	return nil
}

func startSplitTunnel(cfg *wgConfig) (_ *splitTunnel, err error) {
	if len(cfg.ExcludedApps) == 0 {
		return nil, nil
	}
	if runtime.GOARCH != "amd64" {
		return nil, fmt.Errorf("this split tunnel package supports Windows x64 only")
	}
	s := &splitTunnel{handle: windows.InvalidHandle, failures: make(chan error, 1)}
	defer func() {
		if err != nil {
			s.Close()
		}
	}()
	s.manager, err = mgr.Connect()
	if err != nil {
		return nil, err
	}
	// Discover a foreign owner before starting our service with the same device name.
	var foreignName string
	for _, name := range []string{"MullvadSplitTunnel", "mullvad-split-tunnel", "AmneziaVPNSplitTunnel"} {
		other, e := s.manager.OpenService(name)
		if errors.Is(e, windows.ERROR_SERVICE_DOES_NOT_EXIST) {
			continue
		}
		if e != nil {
			return nil, fmt.Errorf("query %s: %w", name, e)
		}
		status, se := other.Query()
		if se != nil {
			other.Close()
			return nil, se
		}
		if status.State == svc.Stopped {
			other.Close()
			continue
		}
		if foreignName != "" {
			other.Close()
			return nil, fmt.Errorf("multiple foreign split drivers are running; close the other VPNs before connecting")
		}
		foreignName = name
		if status.State != svc.Running {
			other.Close()
			return nil, splitConflict(name, "Служба драйвера запускается или останавливается. Повторите подключение чуть позже.")
		}
		config, ce := other.Config()
		other.Close()
		if ce != nil {
			return nil, splitConflict(name, "Не удалось проверить установленный драйвер: "+ce.Error())
		}
		if ce = compatibleSplitBinary(config.BinaryPathName); ce != nil {
			return nil, splitConflict(name, "Проверка совместимости не пройдена. Попытка открыть драйвер не выполнялась: "+ce.Error())
		}
		s.borrowed = true
	}
	if !s.borrowed {
		s.service, err = s.manager.OpenService(splitServiceName)
		if err != nil {
			return nil, fmt.Errorf("split tunnel driver is not installed: run Install.ps1 as administrator: %w", err)
		}
		st, err := s.service.Query()
		if err != nil {
			return nil, err
		}
		stopDeadline := time.Now().Add(5 * time.Second)
		for st.State == svc.StopPending && time.Now().Before(stopDeadline) {
			time.Sleep(100 * time.Millisecond)
			st, err = s.service.Query()
			if err != nil {
				return nil, err
			}
		}
		if st.State == svc.Stopped {
			if err = s.service.Start(); err != nil {
				return nil, fmt.Errorf("start signed split tunnel driver: %w", err)
			}
			s.started = true
		}
		deadline := time.Now().Add(5 * time.Second)
		for {
			st, err = s.service.Query()
			if err != nil {
				return nil, err
			}
			if st.State == svc.Running {
				break
			}
			if time.Now().After(deadline) {
				return nil, fmt.Errorf("split tunnel service did not start")
			}
			time.Sleep(100 * time.Millisecond)
		}
	}
	path, _ := windows.UTF16PtrFromString(`\\.\MULLVADSPLITTUNNEL`)
	s.handle, err = windows.CreateFile(path, windows.GENERIC_READ|windows.GENERIC_WRITE, 0, nil, windows.OPEN_EXISTING, windows.FILE_FLAG_OVERLAPPED, 0)
	if err != nil {
		if s.borrowed {
			return nil, splitConflict(foreignName, "Не удалось получить исключительный доступ к драйверу. Возможно, его использует другой VPN: "+err.Error())
		}
		return nil, fmt.Errorf("open split tunnel device (another VPN may own it): %w", err)
	}
	if s.borrowed {
		state := make([]byte, 8)
		if e := s.ioctl(9, nil, state); e != nil {
			return nil, splitConflict(foreignName, "Не удалось прочитать состояние драйвера: "+e.Error())
		}
		// A free handle does not prove that an existing configuration can be discarded.
		if binary.LittleEndian.Uint64(state) != 1 {
			return nil, splitConflict(foreignName, "В драйвере осталась конфигурация другого VPN. Она не была изменена.")
		}
		log.Printf("[SPLIT] using idle compatible driver from %s", foreignName)
	}
	s.resetOnClose = true
	// Exclusive ownership and a compatible idle foreign driver (or our own driver).
	if err = s.ioctl(11, nil, nil); err != nil {
		return nil, err
	}
	r, _, _ := fwOpen.Call(0, 10, 0, 0, uintptr(unsafe.Pointer(&s.engine)))
	if r != 0 {
		return nil, fmt.Errorf("FwpmEngineOpen0: %#x", r)
	}
	for i := range splitLayerKeys {
		key := &splitLayerKeys[i]
		// Our fixed private keys only. A failed deletion is checked by Add below.
		fwDeleteLayer.Call(s.engine, uintptr(unsafe.Pointer(key)))
		name, _ := windows.UTF16PtrFromString("cronixWG split tunnel")
		layer := splitSubLayer{Key: *key, Name: name, Weight: 0xffff}
		r, _, _ = fwAddLayer.Call(s.engine, uintptr(unsafe.Pointer(&layer)), 0)
		runtime.KeepAlive(name)
		if r != 0 {
			return nil, fmt.Errorf("FwpmSubLayerAdd0: %#x", r)
		}
		s.layers++
	}
	guids := unsafe.Slice((*byte)(unsafe.Pointer(&splitLayerKeys[0])), 32)
	if err = s.ioctl(1, guids, nil); err != nil {
		return nil, fmt.Errorf("initialize pinned driver ABI: %w", err)
	}
	processes, err := snapshotSplitProcesses()
	if err != nil {
		return nil, err
	}
	blob, err := splitBlob(processes, nil, true)
	if err != nil {
		return nil, err
	}
	if err = s.ioctl(3, blob, nil); err != nil {
		return nil, err
	}
	paths := make([]string, 0, len(cfg.ExcludedApps)+1)
	// The transport itself must never enter the tunnel. Bind socket options are
	// retained as well, for full-tunnel mode without the split driver.
	exe, err := os.Executable()
	if err != nil {
		return nil, err
	}
	for _, app := range append(append([]string{}, cfg.ExcludedApps...), exe) {
		p, e := splitDevicePath(app)
		if e != nil {
			return nil, e
		}
		paths = append(paths, p)
	}
	blob, err = splitBlob(nil, paths, false)
	if err != nil {
		return nil, err
	}
	if err = s.ioctl(6, blob, nil); err != nil {
		return nil, err
	}
	if err = s.updateAddresses(cfg, bypassIfIndex.Load()); err != nil {
		return nil, err
	}
	state := make([]byte, 8)
	if err = s.ioctl(9, nil, state); err != nil {
		return nil, err
	}
	if binary.LittleEndian.Uint64(state) != 4 {
		return nil, fmt.Errorf("split driver did not engage (state %d)", binary.LittleEndian.Uint64(state))
	}
	log.Printf("[SPLIT] active: %d excluded applications; ordinary Windows sockets bypass VPN", len(cfg.ExcludedApps))
	s.eventDone = make(chan struct{})
	go s.readEvents()
	return s, nil
}

// Reuse only an audited ABI, never probe unknown versions with mutating IOCTLs.
func compatibleSplitBinary(path string) error {
	path = strings.Trim(strings.TrimSpace(path), `"`)
	src, err := windows.UTF16PtrFromString(path)
	if err != nil {
		return err
	}
	buf := make([]uint16, 32768)
	n, err := windows.ExpandEnvironmentStrings(src, &buf[0], uint32(len(buf)))
	if err != nil {
		return err
	}
	if n == 0 || n > uint32(len(buf)) {
		return fmt.Errorf("invalid driver path length")
	}
	path = strings.TrimPrefix(windows.UTF16ToString(buf), `\??\`)
	if strings.HasPrefix(strings.ToLower(path), `\systemroot\`) {
		root, e := windows.GetWindowsDirectory()
		if e != nil {
			return e
		}
		path = filepath.Join(root, path[len(`\SystemRoot\`):])
	}
	f, err := os.Open(path)
	if err != nil {
		return err
	}
	defer f.Close()
	return verifySplitBinary(f)
}

func splitConflict(service, reason string) error {
	emitEvent("split_conflict", map[string]any{"service": service, "reason": reason})
	return fmt.Errorf("Конфликт драйвера раздельного туннелирования (%s): %s", service, reason)
}

func (s *splitTunnel) readEvents() {
	defer close(s.eventDone)
	event, err := windows.CreateEvent(nil, 1, 0, nil)
	if err != nil {
		s.failures <- err
		return
	}
	defer windows.CloseHandle(event)
	buffer := make([]byte, 128*1024)
	for {
		windows.ResetEvent(event)
		over := windows.Overlapped{HEvent: event}
		var n uint32
		s.eventMu.Lock()
		if s.eventStop {
			s.eventMu.Unlock()
			return
		}
		err = windows.DeviceIoControl(s.handle, splitIOCTL(2), nil, 0, &buffer[0], uint32(len(buffer)), &n, &over)
		s.eventMu.Unlock()
		if errors.Is(err, windows.ERROR_IO_PENDING) {
			err = windows.GetOverlappedResult(s.handle, &over, &n, true)
		}
		if errors.Is(err, windows.ERROR_OPERATION_ABORTED) {
			return
		}
		if err != nil {
			s.failures <- fmt.Errorf("read split tunnel event: %w", err)
			return
		}
		if n < 16 {
			s.failures <- fmt.Errorf("short split driver event")
			return
		}
		id := binary.LittleEndian.Uint32(buffer)
		if id&0x80000000 != 0 {
			s.failures <- fmt.Errorf("split driver reported error event %#x; exclusions could not be applied", id)
			return
		}
		if isDebug {
			log.Printf("[SPLIT] process event %d (%d bytes)", id, n)
		}
	}
}

func splitDevicePath(path string) (string, error) {
	path = filepath.Clean(path)
	if len(path) < 4 || path[1:3] != `:\` {
		return "", fmt.Errorf("excluded EXE must have a full local drive path: %q", path)
	}
	fi, err := os.Stat(path)
	if err != nil || !fi.Mode().IsRegular() {
		return "", fmt.Errorf("excluded EXE does not exist or is not a file: %q", path)
	}
	drive, _ := windows.UTF16PtrFromString(path[:2])
	buf := make([]uint16, 32768)
	_, err = windows.QueryDosDevice(drive, &buf[0], uint32(len(buf)))
	if err != nil {
		return "", err
	}
	return windows.UTF16ToString(buf) + path[2:], nil
}

func snapshotSplitProcesses() ([]splitProcess, error) {
	snapshot, err := windows.CreateToolhelp32Snapshot(windows.TH32CS_SNAPPROCESS, 0)
	if err != nil {
		return nil, err
	}
	defer windows.CloseHandle(snapshot)
	entry := windows.ProcessEntry32{Size: uint32(unsafe.Sizeof(windows.ProcessEntry32{}))}
	var result []splitProcess
	for err = windows.Process32First(snapshot, &entry); err == nil; err = windows.Process32Next(snapshot, &entry) {
		if entry.ProcessID == 0 {
			continue
		}
		p := splitProcess{pid: entry.ProcessID, parent: entry.ParentProcessID}
		handle, e := windows.OpenProcess(windows.PROCESS_QUERY_LIMITED_INFORMATION, false, entry.ProcessID)
		if e == nil {
			buf := make([]uint16, 32768)
			n := uint32(len(buf))
			// PROCESS_NAME_NATIVE = 1, so paths match the driver's process callbacks.
			if windows.QueryFullProcessImageName(handle, 1, &buf[0], &n) == nil {
				p.path = windows.UTF16ToString(buf[:n])
			}
			windows.CloseHandle(handle)
		}
		result = append(result, p)
	}
	if !errors.Is(err, windows.ERROR_NO_MORE_FILES) {
		return nil, err
	}
	return result, nil
}

func internetAddresses(index uint32) (v4, v6 netip.Addr, err error) {
	iface, err := net.InterfaceByIndex(int(index))
	if err != nil {
		return v4, v6, err
	}
	addresses, err := iface.Addrs()
	if err != nil {
		return v4, v6, err
	}
	for _, a := range addresses {
		p, e := netip.ParsePrefix(a.String())
		if e != nil {
			continue
		}
		ip := p.Addr()
		if !ip.IsGlobalUnicast() || ip.IsLinkLocalUnicast() {
			continue
		}
		if ip.Is4() && !v4.IsValid() {
			v4 = ip
		}
		if ip.Is6() && !v6.IsValid() {
			v6 = ip
		}
	}
	if !v4.IsValid() {
		err = fmt.Errorf("internet interface %d has no usable IPv4 address", index)
	}
	return
}
func (s *splitTunnel) updateAddresses(cfg *wgConfig, index uint32) error {
	if s == nil {
		return nil
	}
	v4, v6, err := internetAddresses(index)
	if err != nil {
		return err
	}
	b := make([]byte, 40)
	for _, p := range cfg.Addresses {
		if p.Addr().Is4() {
			copy(b[0:4], p.Addr().AsSlice())
		} else {
			copy(b[8:24], p.Addr().AsSlice())
		}
	}
	copy(b[4:8], v4.AsSlice())
	if v6.IsValid() {
		copy(b[24:40], v6.AsSlice())
	}
	if s.haveAddresses && string(s.lastAddresses[:]) == string(b) {
		return nil
	}
	if err := s.ioctl(4, b, nil); err != nil {
		return err
	}
	copy(s.lastAddresses[:], b)
	s.haveAddresses = true
	return nil
}
func (s *splitTunnel) Close() {
	if s == nil {
		return
	}
	s.closeOnce.Do(func() {
		if s.eventDone != nil {
			s.eventMu.Lock()
			s.eventStop = true
			windows.CancelIoEx(s.handle, nil)
			s.eventMu.Unlock()
			<-s.eventDone
		}
		if s.handle != windows.InvalidHandle {
			if s.resetOnClose {
				if err := s.ioctl(11, nil, nil); err != nil {
					log.Printf("[SPLIT] reset: %v", err)
				}
			}
			windows.CloseHandle(s.handle)
		}
		if s.service != nil && (s.started || s.handle != windows.InvalidHandle) {
			if _, err := s.service.Control(svc.Stop); err != nil {
				log.Printf("[SPLIT] stop driver service: %v", err)
			} else {
				deadline := time.Now().Add(3 * time.Second)
				for time.Now().Before(deadline) {
					status, err := s.service.Query()
					if err != nil || status.State == svc.Stopped {
						break
					}
					time.Sleep(50 * time.Millisecond)
				}
			}
		}
		if s.engine != 0 {
			for i := 0; i < s.layers; i++ {
				fwDeleteLayer.Call(s.engine, uintptr(unsafe.Pointer(&splitLayerKeys[i])))
			}
			fwClose.Call(s.engine)
		}
		if s.service != nil {
			s.service.Close()
		}
		if s.manager != nil {
			s.manager.Disconnect()
		}
	})
}

// Re-evaluate default routes excluding our own TUN. The caller updates both the
// driver addresses and transport socket binding; TURN workers reconnect normally.
func currentInternetInterface(tunnel winipcfg.LUID) (uint32, error) {
	routes, err := winipcfg.GetIPForwardTable2(windows.AF_INET)
	if err != nil {
		return 0, err
	}
	best := uint64(^uint64(0))
	var index uint32
	for _, r := range routes {
		if r.InterfaceLUID == tunnel || r.DestinationPrefix.Prefix().Bits() != 0 || r.Loopback {
			continue
		}
		iface, e := net.InterfaceByIndex(int(r.InterfaceIndex))
		if e != nil || iface.Flags&net.FlagUp == 0 || strings.EqualFold(iface.Name, wgAdapterName) {
			continue
		}
		row, e := r.InterfaceLUID.IPInterface(windows.AF_INET)
		if e != nil {
			continue
		}
		metric := uint64(r.Metric) + uint64(row.Metric)
		if metric < best {
			best = metric
			index = r.InterfaceIndex
		}
	}
	if index == 0 {
		return 0, fmt.Errorf("no internet default route outside VPN")
	}
	return index, nil
}
