#pragma once

#include <cstdint>

enum wgb_log_level
{
	error = 0,
	warning = 1, // Consider adding a warning level if applicable
	info = 2,
	debug = 4,
	all = 255
};

enum wgb_log_verbosity
{
    none = 0x00,  ///< No additional information (message only)
    timestamp = 0x01,  ///< Include timestamp in log output
    thread = 0x02,  ///< Include thread ID in log output
    logger = 0x04,  ///< Include logger name in log output
    path = 0x08,  ///< Include source file path/location in log output
    level = 0x10,  ///< Include log level in output
    full = 0x1F,  ///< Include all available information (default)
};

/// <summary>
/// Wireguard event types for the logging
/// </summary>
enum class wg_tunnel_event_type : uint32_t
{
    handshake_sent = 0,
    handshake_response_received,
    data_sent,
    data_received,
    tunnel_error,
    network_config_change,
    connected,
    disconnected,
    socks5_connected,
    socks5_disconnected,
    socks5_error
};

/**
 * @brief Network lock mode enumeration.
 *
 * Defines the possible states for the network lock feature:
 * - wgb_network_lock_disabled: Network lock is disabled (no traffic filtering).
 * - wgb_network_lock_enabled: Network lock is enabled (traffic leaks prevented).
 */
enum wgb_network_lock_mode
{
    wgb_network_lock_disabled = 0,
    wgb_network_lock_enabled = 1
};

/// <summary>
/// Wireguard event type
/// </summary>
struct wg_tunnel_event
{
    wg_tunnel_event_type type; // event type
    uint32_t status; // status or error code
    size_t data; // optional data
};

struct wgb_stats
{
    int64_t time_since_last_handshake;
    uint64_t tx_bytes;
    uint64_t rx_bytes;
    float estimated_loss;
    int32_t estimated_rtt; // rtt estimated on time it took to complete latest initiated handshake in ms
};

struct wgb_interface
{
    char* private_key; // required
    char* address; // required
    char* dns; // optional
    char* mtu; // optional
    char* listen_port; // optional
};

struct wgb_interface_ex
{
    char* private_key; // required
    char* address; // required
    char* dns; // optional
    char* mtu; // optional
    char* listen_port; // optional

    char* script_timeout; // optional, The script execution timeout in seconds.
    char* pre_up; // optional, PreUp scripts are executed before the WireGuard tunnel is brought up.
    char* post_up; // optional, PostUp scripts are executed after the WireGuard tunnel is brought up.
    char* pre_down; // optional, PreDown scripts are executed before the WireGuard tunnel is brought down.
    char* post_down; // optional, PostDown scripts are executed after the WireGuard tunnel is brought down.

    // amnezia
    struct
    {
        // pre handshake
        char* Jc; //optional
        char* Jmin; //optional
        char* Jmax; //optional
        char* Jd; //optional

        char* Id; //optional
        char* Ip; //optional
        char* Ib; //optional

        char* S1; //optional
        char* S2; //optional
        char* S3; //optional
        char* S4; //optional
        char* H1; //optional
        char* H2; //optional
        char* H3; //optional
        char* H4; //optional
    } amnezia;
};

struct wgb_peer
{
    char* public_key; // required
    char* preshared_key; // optional
    char* allowed_ips; // required
    char* endpoint; // required
    uint32_t persistent_keep_alive; // optional
};

struct wgb_extra
{
    wchar_t* allowed_apps;// optional
    char* ignored_ips; // optional
    char* socks5_proxy; //optional
};

struct wgb_extra_ex
{
    size_t size; // size of structure
    wchar_t* allowed_apps;// optional
    char* ignored_ips; // optional

    wchar_t* disallowed_apps;// optional

    // socks5 proxy support
    struct
    {
        char* proxy; //optional
        char* username; //optional
        char* password; //optional
        char* all_traffic; //optional
    } socks5;
};

void __stdcall wgb_set_log_verbosity(wgb_log_verbosity verbosity);

HANDLE __stdcall wgb_get_handle(void (*log_printer)(const char*), wgb_log_level level, bool enable_traffic_capture);
HANDLE __stdcall wgb_get_handle_ex(void (*log_printer)(const char*), wgb_log_level level, void (*event_logger)(wg_tunnel_event), const bool enable_traffic_capture, const bool enable_analytics);
void __stdcall wgb_release_handle(HANDLE wgbooster_handle);
void __stdcall wgb_set_log_level(HANDLE wgbooster_handle, wgb_log_level level);
BOOL __stdcall wgb_create_tunnel_from_file(HANDLE wgbooster_handle, const char* file_name);
BOOL __stdcall wgb_create_tunnel_from_file_w(HANDLE wgbooster_handle, const wchar_t* file_name);
BOOL __stdcall wgb_create_tunnel(HANDLE wgbooster_handle, const wchar_t* config_name, wgb_interface* interface_settings, wgb_peer* peer_settings, wgb_extra* extra);
BOOL __stdcall wgb_create_tunnel_ex(HANDLE wgbooster_handle, const wchar_t* config_name, const wgb_interface_ex* interface_settings, const wgb_peer* peer_settings, const wgb_extra_ex* extra);
BOOL __stdcall wgb_drop_tunnel(HANDLE wgbooster_handle, BOOL preserve_network_lock);
BOOL __stdcall wgb_start_tunnel(HANDLE wgbooster_handle);
BOOL __stdcall wgb_stop_tunnel(HANDLE wgbooster_handle);
wgb_stats __stdcall wgb_get_tunnel_state(HANDLE wgbooster_handle);
BOOL __stdcall wgb_get_tunnel_active(HANDLE wgbooster_handle);
BOOL __stdcall wgb_drop_all_tcp_sockets(HANDLE wgbooster_handle);
BOOL __stdcall wgb_set_network_lock_mode(HANDLE wgbooster_handle, wgb_network_lock_mode mode);
wgb_network_lock_mode __stdcall wgb_get_network_lock_mode(HANDLE wgbooster_handle);

HANDLE __stdcall wgbp_get_handle(void (*log_printer)(const char*), wgb_log_level level, bool enable_traffic_capture);
HANDLE __stdcall wgbp_get_handle_ex(void (*log_printer)(const char*), wgb_log_level level, void (*event_logger)(wg_tunnel_event), const bool enable_traffic_capture, const bool enable_analytics);
void __stdcall wgbp_release_handle(HANDLE wgbooster_handle);
void __stdcall wgbp_set_log_level(HANDLE wgbooster_handle, wgb_log_level level);
BOOL __stdcall wgbp_create_tunnel_from_file(HANDLE wgbooster_handle, const char* file_name);
BOOL __stdcall wgbp_create_tunnel_from_file_w(HANDLE wgbooster_handle, const wchar_t* file_name);
BOOL __stdcall wgbp_create_tunnel(HANDLE wgbooster_handle, const wchar_t* config_name, wgb_interface* interface_settings, wgb_peer* peer_settings, wgb_extra* extra);
BOOL __stdcall wgbp_create_tunnel_ex(HANDLE wgbooster_handle, const wchar_t* config_name, const wgb_interface_ex* interface_settings, const wgb_peer* peer_settings, const wgb_extra_ex* extra);
BOOL __stdcall wgbp_drop_tunnel(HANDLE wgbooster_handle, BOOL preserve_network_lock);
BOOL __stdcall wgbp_start_tunnel(HANDLE wgbooster_handle);
BOOL __stdcall wgbp_stop_tunnel(HANDLE wgbooster_handle);
wgb_stats __stdcall wgbp_get_tunnel_state(HANDLE wgbooster_handle);
BOOL __stdcall wgbp_get_tunnel_active(HANDLE wgbooster_handle);
BOOL __stdcall wgbp_drop_all_tcp_sockets(HANDLE wgbooster_handle);
BOOL __stdcall wgbp_set_network_lock_mode(HANDLE wgbooster_handle, wgb_network_lock_mode mode);
wgb_network_lock_mode __stdcall wgbp_get_network_lock_mode(HANDLE wgbooster_handle);

BOOL __stdcall wgb_set_global_option(const wchar_t* option, const wchar_t* value);

/**
 * @brief Resets the network lock driver state to default.
 *
 * This function resets the network lock driver state by clearing any existing
 * filters and restoring all network adapters to their normal operating mode.
 * It is useful for cleanup scenarios when the application crashes or exits
 * abnormally without properly releasing network lock resources.
 *
 * @return TRUE if the reset was successful, FALSE otherwise.
 *
 * @error ERROR_GEN_FAILURE if the NDIS driver is not loaded or inaccessible.
 *
 * @note This function does not require a tunnel handle - it operates globally.
 * @note Safe to call even if no network lock was previously enabled.
 * @note Thread-safe operation.
 */
BOOL __stdcall wg_reset_network_lock();

/**
 * @brief Queries whether network lock is currently active at the driver level.
 *
 * This function checks if any network adapter is currently in tunnel filtering mode,
 * which indicates that network lock (kill switch) is active. This is useful for
 * recovery scenarios when the application restarts after a crash and needs to
 * determine if network lock was previously enabled and is still active.
 *
 * The function queries all network adapters bound to the NDIS filter driver and
 * checks their operational mode. If any adapter has tunnel mode flags set
 * (MSTCP_FLAG_SENT_TUNNEL | MSTCP_FLAG_RECV_TUNNEL), network lock is considered active.
 *
 * @return TRUE if network lock is currently active (at least one adapter is in tunnel mode),
 *         FALSE if network lock is not active or if the driver is not accessible.
 *
 * @error ERROR_GEN_FAILURE if the NDIS driver is not loaded or inaccessible.
 *
 * @note This function does not require a tunnel handle - it operates globally.
 * @note Thread-safe operation.
 * @note Useful for crash recovery to detect orphaned network lock state.
 *
 * @see wg_reset_network_lock() for resetting the network lock state.
 * @see wgb_set_network_lock_mode() for enabling/disabling network lock.
 */
BOOL __stdcall wg_is_network_lock_active();
