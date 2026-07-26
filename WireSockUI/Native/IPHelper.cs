using System;
using System.Runtime.InteropServices;

namespace WireSockUI.Native
{
    /// <summary>
    ///     Native class to validate IPv4, IPv6 and DNS domain addresses & CIDR notations
    /// </summary>
    internal static class IpHelper
    {
        [DllImport("iphlpapi.dll", SetLastError = false, ExactSpelling = true)]
        private static extern uint ParseNetworkString([MarshalAs(UnmanagedType.LPWStr)] string networkString,
            NetString types, [Optional] IntPtr addressInfo, [Optional] IntPtr portNumber,
            [Optional] IntPtr prefixLength);

        /// <summary>
        ///     Determine if <paramref name="ipNetwork" /> is a valid IPv4 or IPv6 CIDR network notation
        /// </summary>
        /// <param name="ipNetwork">IPv4/IPv6 CIDR notation</param>
        /// <returns><c>true</c> if valid, otherwise <c>false</c></returns>
        public static bool IsValidIpNetwork(string ipNetwork)
        {
            if (string.IsNullOrWhiteSpace(ipNetwork))
                return false;

            return ParseNetworkString(ipNetwork.Trim(), NetString.NetStringIpNetwork) == 0;
        }

        /// <summary>
        ///     Determines if the given string is a valid CIDR notation representing an IPv4 or IPv6 address with a subnet mask.
        /// </summary>
        /// <param name="cidr">The CIDR notation string to validate.</param>
        /// <returns><c>true</c> if the input string is a valid CIDR notation, otherwise <c>false</c>.</returns>
        /// <remarks>
        ///     This method checks if the input string is in the format of an IPv4 or IPv6 address, followed by a forward slash and
        ///     a subnet mask.
        ///     It validates the IP address, the subnet mask, and the address family (IPv4 or IPv6).
        /// </remarks>
        public static bool IsValidCidr(string cidr)
        {
            if (string.IsNullOrWhiteSpace(cidr))
                return false;

            var parts = cidr.Trim().Split('/');
            if (parts.Length != 2) return false;

            if (!TryParsePrefixLength(parts[1], out var prefixLength)) return false;

            if (ParseNetworkString(parts[0], NetString.NetStringIpv4Address) == 0 &&
                0 <= prefixLength && prefixLength <= 32)
                return true;

            if (ParseNetworkString(parts[0], NetString.NetStringIpv6Address) == 0 &&
                0 <= prefixLength && prefixLength <= 128)
                return true;

            return false;
        }

        /// <summary>
        ///     Determine if <paramref name="ipAddress" /> is a valid IPv4 or IPv6 address
        /// </summary>
        /// <param name="ipAddress">IPv4/IPv6 address</param>
        /// <returns><c>true</c> if valid, otherwise <c>false</c></returns>
        public static bool IsValidIpAddress(string ipAddress)
        {
            if (string.IsNullOrWhiteSpace(ipAddress))
                return false;

            return ParseNetworkString(ipAddress.Trim(), NetString.NetStringIpAddress) == 0;
        }

        /// <summary>
        ///     Determines if the given string is a valid IP address or a valid CIDR notation.
        /// </summary>
        /// <param name="interfaceAddress">The interface address string to validate.</param>
        /// <returns><c>true</c> if the input string is a valid IP address or a valid CIDR notation, otherwise <c>false</c>.</returns>
        /// <remarks>
        ///     This method uses the IsValidIpAddress and IsValidCidr methods to validate the input string.
        /// </remarks>
        public static bool IsValidSubnetOrSingleIpAddress(string interfaceAddress)
        {
            return IsValidIpAddress(interfaceAddress) || IsValidCidr(interfaceAddress);
        }

        /// <summary>
        ///     Determine if <paramref name="address" /> is a valid IPv4, IPv6 address or DNS domain
        /// </summary>
        /// <param name="address">IPv4/IPv6 address or DNS domain, optionally with port specification</param>
        /// <returns><c>true</c> if valid, otherwise <c>false</c></returns>
        public static bool IsValidAddress(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                return false;

            return ParseNetworkString(address.Trim(), NetString.NetStringAnyServiceNoScope) == 0;
        }

        private static bool TryParsePrefixLength(string value, out int prefixLength)
        {
            prefixLength = 0;
            if (value == null)
                return false;

            var trimmed = value.Trim();
            if (trimmed.Length == 0)
                return false;

            foreach (var character in trimmed)
            {
                if (character < '0' || character > '9')
                    return false;

                prefixLength = prefixLength * 10 + character - '0';
                if (prefixLength > 128)
                    return false;
            }

            return true;
        }

        [Flags]
        private enum NetString
        {
            /// <summary>
            ///     The NetworkString parameter points to an IPv4 address using Internet standard dotted-decimal notation. A network
            ///     port or
            ///     prefix must not be present in the network string.
            ///     <para>An example network string is the following:</para>
            ///     <para>192.168.100.10</para>
            /// </summary>
            NetStringIpv4Address = 0x00000001,

            /// <summary>
            ///     The NetworkString parameter points to an IPv4 service using Internet standard dotted-decimal notation. A network
            ///     port is
            ///     required as part of the network string. A prefix must not be present in the network string.
            ///     <para>An example network string is the following:</para>
            ///     <para>192.168.100.10:80</para>
            /// </summary>
            NetStringIpv4Service = 0x00000002,

            /// <summary>
            ///     The NetworkString parameter points to an IPv4 network using Internet standard dotted-decimal notation. A network
            ///     prefix that
            ///     uses the Classless Inter-Domain Routing (CIDR) notation is required as part of the network string. A network port
            ///     must not be
            ///     present in the network string.
            ///     <para>An example network string is the following:</para>
            ///     <para>192.168.100/24</para>
            /// </summary>
            NetStringIpv4Network = 0x00000004,

            /// <summary>
            ///     The NetworkString parameter points to an IPv6 address using Internet standard hexadecimal encoding. An IPv6 scope
            ///     ID may be
            ///     present in the network string. A network port or prefix must not be present in the network string.
            ///     <para>An example network string is the following:</para>
            ///     <para>21DA:00D3:0000:2F3B:02AA:00FF:FE28:9C5A%2</para>
            /// </summary>
            NetStringIpv6Address = 0x00000008,

            /// <summary>
            ///     The NetworkString parameter points to an IPv6 address using Internet standard hexadecimal encoding. An IPv6 scope
            ///     ID must not
            ///     be present in the network string. A network port or prefix must not be present in the network string.
            ///     <para>An example network string is the following:</para>
            ///     <para>21DA:00D3:0000:2F3B:02AA:00FF:FE28:9C5A</para>
            /// </summary>
            NetStringIpv6AddressNoScope = 0x00000010,

            /// <summary>
            ///     The NetworkString parameter points to an IPv6 service using Internet standard hexadecimal encoding. A network port
            ///     is
            ///     required as part of the network string. An IPv6 scope ID may be present in the network string. A prefix must not be
            ///     present
            ///     in the network string.
            ///     <para>An example network string with a scope ID is the following:</para>
            ///     <para>[21DA:00D3:0000:2F3B:02AA:00FF:FE28:9C5A%2]:8080</para>
            /// </summary>
            NetStringIpv6Service = 0x00000020,

            /// <summary>
            ///     The NetworkString parameter points to an IPv6 service using Internet standard hexadecimal encoding. A network port
            ///     is
            ///     required as part of the network string. An IPv6 scope ID must not be present in the network string. A prefix must
            ///     not be
            ///     present in the network string.
            ///     <para>An example network string is the following:</para>
            ///     <para>21DA:00D3:0000:2F3B:02AA:00FF:FE28:9C5A:8080</para>
            /// </summary>
            NetStringIpv6ServiceNoScope = 0x00000040,

            /// <summary>
            ///     The NetworkString parameter points to an IPv6 network using Internet standard hexadecimal encoding. A network
            ///     prefix in CIDR
            ///     notation is required as part of the network string. A network port or scope ID must not be present in the network
            ///     string.
            ///     <para>An example network string is the following:</para>
            ///     <para>21DA:D3::/48</para>
            /// </summary>
            NetStringIpv6Network = 0x00000080,

            /// <summary>
            ///     The NetworkString parameter points to an Internet address using a Domain Name System (DNS) name. A network port or
            ///     prefix
            ///     must not be present in the network string.
            ///     <para>An example network string is the following:</para>
            ///     <para>www.microsoft.com</para>
            /// </summary>
            NetStringNamedAddress = 0x00000100,

            /// <summary>
            ///     The NetworkString parameter points to an Internet service using a DNS name. A network port must be present in the
            ///     network string.
            ///     <para>An example network string is the following:</para>
            ///     <para>www.microsoft.com:80</para>
            /// </summary>
            NetStringNamedService = 0x00000200,

            /// <summary>
            ///     The NetworkString parameter points to an IPv4 address using Internet standard dotted-decimal notation or an IPv6
            ///     address
            ///     using the Internet standard hexadecimal encoding. An IPv6 scope ID may be present in the network string. A network
            ///     port or
            ///     prefix must not be present in the network string.
            ///     <para>This type matches either the NET_STRING_IPV4_ADDRESS or NET_STRING_IPV6_ADDRESS types.</para>
            /// </summary>
            NetStringIpAddress = 0x00000009,

            /// <summary>
            ///     The NetworkString parameter points to an IPv4 address using Internet standard dotted-decimal notation or an IPv6
            ///     address
            ///     using Internet standard hexadecimal encoding. An IPv6 scope ID must not be present in the network string. A network
            ///     port or
            ///     prefix must not be present in the network string.
            ///     <para>This type matches either the NET_STRING_IPV4_ADDRESS or NET_STRING_IPV6_ADDRESS_NO_SCOPE types.</para>
            /// </summary>
            NetStringIpAddressNoScope = 0x00000011,

            /// <summary>
            ///     The NetworkString parameter points to an IPv4 service or IPv6 service. A network port is required as part of the
            ///     network
            ///     string. An IPv6 scope ID may be present in the network string. A prefix must not be present in the network string.
            ///     <para>This type matches either the NET_STRING_IPV4_SERVICE or NET_STRING_IPV6_SERVICE types.</para>
            /// </summary>
            NetStringIpService = 0x00000022,

            /// <summary>
            ///     The NetworkString parameter points to an IPv4 service or IPv6 service. A network port is required as part of the
            ///     network
            ///     string. An IPv6 scope ID must not be present in the network string. A prefix must not be present in the network
            ///     string.
            ///     <para>This type matches either the NET_STRING_IPV4_SERVICE or NET_STRING_IPV6_SERVICE_NO_SCOPE types.</para>
            /// </summary>
            NetStringIpServiceNoScope = 0x00000042,

            /// <summary>
            ///     The NetworkString parameter points to an IPv4 or IPv6 network. A network prefix in CIDR notation is required as
            ///     part of the
            ///     network string. A network port or scope ID must not be present in the network.
            ///     <para>This type matches either the NET_STRING_IPV4_NETWORK or NET_STRING_IPV6_NETWORK types.</para>
            /// </summary>
            NetStringIpNetwork = 0x00000084,

            /// <summary>
            ///     The NetworkString parameter points to an IPv4 address in Internet standard dotted-decimal notation, an IPv6 address
            ///     in
            ///     Internet standard hexadecimal encoding, or a DNS name. An IPv6 scope ID may be present in the network string for an
            ///     IPv6
            ///     address. A network port or prefix must not be present in the network string.
            ///     <para>This type matches either the NET_STRING_NAMED_ADDRESS or NET_STRING_IP_ADDRESS types.</para>
            /// </summary>
            NetStringAnyAddress = 0x00000209,

            /// <summary>
            ///     The NetworkString parameter points to an IPv4 address in Internet standard dotted-decimal notation, an IPv6 address
            ///     in
            ///     Internet standard hexadecimal encoding, or a DNS name. An IPv6 scope ID must not be present in the network string
            ///     for an IPv6
            ///     address. A network port or prefix must not be present in the network string.
            ///     <para>This type matches either the NET_STRING_NAMED_ADDRESS or NET_STRING_IP_ADDRESS_NO_SCOPE types.</para>
            /// </summary>
            NetStringAnyAddressNoScope = 0x00000211,

            /// <summary>
            ///     The NetworkString parameter points to an IPv4 service or IPv6 service using IP address notation or a DNS name. A
            ///     network port
            ///     is required as part of the network string. An IPv6 scope ID may be present in the network string. A prefix must not
            ///     be
            ///     present in the network string.
            ///     <para>This type matches either the NET_STRING_NAMED_SERVICE or NET_STRING_IP_SERVICE types.</para>
            /// </summary>
            NetStringAnyService = 0x00000222,

            /// <summary>
            ///     The NetworkString parameter points to an IPv4 service or IPv6 service using IP address notation or a DNS name. A
            ///     network port
            ///     is required as part of the network string. An IPv6 scope ID must not be present in the network string. A prefix
            ///     must not be
            ///     present in the network string.
            ///     <para>This type matches either the NET_STRING_NAMED_SERVICE or NET_STRING_IP_SERVICE_NO_SCOPE types.</para>
            /// </summary>
            NetStringAnyServiceNoScope = 0x00000242
        }
    }
}
