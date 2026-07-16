/* Ported parts from Java to C# and refactored by Hans Wolff, 17/09/2013 */

/* Ported from C to Java by Dmitry Skiba [sahn0], 23/02/08.
 * Original: http://code.google.com/p/curve25519-java/
 */

/* Generic 64-bit integer implementation of Curve25519 ECDH
 * Written by Matthijs van Duin, 200608242056
 * Public domain.
 *
 * Based on work by Daniel J Bernstein, http://cr.yp.to/ecdh.html
 */

using System;
using System.Security.Cryptography;

namespace WireSockUI.Config
{
    public class Curve25519
    {
        /* key size */
        public const int KeySize = 32;

        /********************* radix 2^25.5 GF(2^255-19) math *********************/

        private const int P25 = 33554431; /* (1 << 25) - 1 */
        private const int P26 = 67108863; /* (1 << 26) - 1 */

        /* group order (a prime near 2^252+2^124) */
        private static readonly byte[] Order =
        {
            237, 211, 245, 92,
            26, 99, 18, 88,
            214, 156, 247, 162,
            222, 249, 222, 20,
            0, 0, 0, 0,
            0, 0, 0, 0,
            0, 0, 0, 0,
            0, 0, 0, 16
        };

        /// <summary>
        ///     Smallest multiple of the order that's >= 2^255
        /// </summary>
        private static readonly byte[] OrderTimes8 =
        {
            104, 159, 174, 231,
            210, 24, 147, 192,
            178, 230, 188, 23,
            245, 206, 247, 166,
            0, 0, 0, 0,
            0, 0, 0, 0,
            0, 0, 0, 0,
            0, 0, 0, 128
        };

        /// <summary>
        ///     Constant 1/(2Gy)
        /// </summary>
        private static readonly Long10 BaseR2Y = new Long10(
            5744, 8160848, 4790893, 13779497, 35730846,
            12541209, 49101323, 30047407, 40071253, 6226132
        );

        /********* KEY AGREEMENT *********/

        /// <summary>
        ///     Private key clamping (inline, for performance)
        /// </summary>
        /// <param name="key">[out] 32 random bytes</param>
        public static void ClampPrivateKeyInline(byte[] key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (key.Length != 32)
                throw new ArgumentException($@"key must be 32 bytes long (but was {key.Length} bytes long)");

            key[31] &= 0x7F;
            key[31] |= 0x40;
            key[0] &= 0xF8;
        }

        /// <summary>
        ///     Private key clamping
        /// </summary>
        /// <param name="rawKey">[out] 32 random bytes</param>
        public static byte[] ClampPrivateKey(byte[] rawKey)
        {
            if (rawKey == null) throw new ArgumentNullException(nameof(rawKey));
            if (rawKey.Length != 32)
                throw new ArgumentException(
                    $@"rawKey must be 32 bytes long (but was {rawKey.Length} bytes long)", nameof(rawKey));

            var res = new byte[32];
            Array.Copy(rawKey, res, 32);

            res[31] &= 0x7F;
            res[31] |= 0x40;
            res[0] &= 0xF8;

            return res;
        }

        /// <summary>
        ///     Creates a random private key
        /// </summary>
        /// <returns>32 random bytes that are clamped to a suitable private key</returns>
        public static byte[] CreateRandomPrivateKey()
        {
            var privateKey = new byte[32];
            using (var random = RandomNumberGenerator.Create())
                random.GetBytes(privateKey);
            ClampPrivateKeyInline(privateKey);

            return privateKey;
        }

        /// <summary>
        ///     Key-pair generation (inline, for performance)
        /// </summary>
        /// <param name="publicKey">[out] public key</param>
        /// <param name="signingKey">[out] signing key (ignored if NULL)</param>
        /// <param name="privateKey">[out] private key</param>
        /// <remarks>WARNING: if signingKey is not NULL, this function has data-dependent timing</remarks>
        public static void KeyGenInline(byte[] publicKey, byte[] signingKey, byte[] privateKey)
        {
            if (publicKey == null) throw new ArgumentNullException(nameof(publicKey));
            if (publicKey.Length != 32)
                throw new ArgumentException(
                    $@"publicKey must be 32 bytes long (but was {publicKey.Length} bytes long)",
                    nameof(publicKey));

            if (signingKey == null) throw new ArgumentNullException(nameof(signingKey));
            if (signingKey.Length != 32)
                throw new ArgumentException(
                    $@"signingKey must be 32 bytes long (but was {signingKey.Length} bytes long)",
                    nameof(signingKey));

            if (privateKey == null) throw new ArgumentNullException(nameof(privateKey));
            if (privateKey.Length != 32)
                throw new ArgumentException(
                    $@"privateKey must be 32 bytes long (but was {privateKey.Length} bytes long)",
                    nameof(privateKey));

            using (var random = RandomNumberGenerator.Create())
                random.GetBytes(privateKey);
            ClampPrivateKeyInline(privateKey);

            Core(publicKey, signingKey, privateKey, null);
        }

        /// <summary>
        ///     Generates the public key out of the clamped private key
        /// </summary>
        /// <param name="privateKey">private key (must use ClampPrivateKey first!)</param>
        public static byte[] GetPublicKey(byte[] privateKey)
        {
            var publicKey = new byte[32];

            Core(publicKey, null, privateKey, null);
            return publicKey;
        }

        /// <summary>
        ///     Generates signing key out of the clamped private key
        /// </summary>
        /// <param name="privateKey">private key (must use ClampPrivateKey first!)</param>
        public static byte[] GetSigningKey(byte[] privateKey)
        {
            var signingKey = new byte[32];
            var publicKey = new byte[32];

            Core(publicKey, signingKey, privateKey, null);
            return signingKey;
        }

        /// <summary>
        ///     Key agreement
        /// </summary>
        /// <param name="privateKey">[in] your private key for key agreement</param>
        /// <param name="peerPublicKey">[in] peer's public key</param>
        /// <returns>shared secret (needs hashing before use)</returns>
        public static byte[] GetSharedSecret(byte[] privateKey, byte[] peerPublicKey)
        {
            var sharedSecret = new byte[32];

            Core(sharedSecret, null, privateKey, peerPublicKey);
            return sharedSecret;
        }

        /********************* radix 2^8 math *********************/

        private static void Copy32(byte[] source, byte[] destination)
        {
            Array.Copy(source, 0, destination, 0, 32);
        }

        /* p[m..n+m-1] = q[m..n+m-1] + z * x */
        /* n is the size of x */
        /* n+m is the size of p and q */

        private static int MultiplyArraySmall(byte[] p, byte[] q, int m, byte[] x, int n, int z)
        {
            var v = 0;
            for (var i = 0; i < n; ++i)
            {
                v += (q[i + m] & 0xFF) + z * (x[i] & 0xFF);
                p[i + m] = (byte)v;
                v >>= 8;
            }

            return v;
        }

        /* p += x * y * z  where z is a small integer
         * x is size 32, y is size t, p is size 32+t
         * y is allowed to overlap with p+32 if you don't care about the upper half  */

        private static void MultiplyArray32(byte[] p, byte[] x, byte[] y, int t, int z)
        {
            const int n = 31;
            var w = 0;
            var i = 0;
            for (; i < t; i++)
            {
                var zy = z * (y[i] & 0xFF);
                w += MultiplyArraySmall(p, p, i, x, n, zy) +
                     (p[i + n] & 0xFF) + zy * (x[n] & 0xFF);
                p[i + n] = (byte)w;
                w >>= 8;
            }

            p[i + n] = (byte)(w + (p[i + n] & 0xFF));
        }

        /* divide r (size n) by d (size t), returning quotient q and remainder r
         * quotient is size n-t+1, remainder is size t
         * requires t > 0 && d[t-1] != 0
         * requires that r[-1] and d[-1] are valid memory locations
         * q may overlap with r+t */
        private static void DivMod(byte[] q, byte[] r, int n, byte[] d, int t)
        {
            var rn = 0;
            var dt = (d[t - 1] & 0xFF) << 8;
            if (t > 1) dt |= d[t - 2] & 0xFF;
            while (n-- >= t)
            {
                var z = (rn << 16) | ((r[n] & 0xFF) << 8);
                if (n > 0) z |= r[n - 1] & 0xFF;
                z /= dt;
                rn += MultiplyArraySmall(r, r, n - t + 1, d, t, -z);
                q[n - t + 1] = (byte)((z + rn) & 0xFF); /* rn is 0 or -1 (underflow) */
                MultiplyArraySmall(r, r, n - t + 1, d, t, -rn);
                rn = r[n] & 0xFF;
                r[n] = 0;
            }

            r[t - 1] = (byte)rn;
        }

        private static int GetNumSize(byte[] num, int maxSize)
        {
            for (var i = maxSize; i >= 0; i++)
                if (num[i] == 0)
                    return i + 1;
            return 0;
        }

        /// <summary>
        ///     Returns x if a contains the gcd, y if b.
        /// </summary>
        /// <param name="x">x and y must have 64 bytes space for temporary use.</param>
        /// <param name="y">x and y must have 64 bytes space for temporary use.</param>
        /// <param name="a">requires that a[-1] and b[-1] are valid memory locations</param>
        /// <param name="b">requires that a[-1] and b[-1] are valid memory locations</param>
        /// <returns>Also, the returned buffer contains the inverse of a mod b as 32-byte signed.</returns>
        private static byte[] Egcd32(byte[] x, byte[] y, byte[] a, byte[] b)
        {
            var bn = 32;
            int i;
            for (i = 0; i < 32; i++)
                x[i] = y[i] = 0;
            x[0] = 1;
            var an = GetNumSize(a, 32);
            if (an == 0)
                return y; /* division by zero */
            var temp = new byte[32];
            while (true)
            {
                var qn = bn - an + 1;
                DivMod(temp, b, bn, a, an);
                bn = GetNumSize(b, bn);
                if (bn == 0)
                    return x;
                MultiplyArray32(y, x, temp, qn, -1);

                qn = an - bn + 1;
                DivMod(temp, a, an, b, bn);
                an = GetNumSize(a, an);
                if (an == 0)
                    return y;
                MultiplyArray32(x, y, temp, qn, -1);
            }
        }

        /* Convert to internal format from little-endian byte format */

        private static void Unpack(Long10 x, byte[] m)
        {
            x.N0 = (m[0] & 0xFF) | ((m[1] & 0xFF) << 8) |
                   ((m[2] & 0xFF) << 16) | ((m[3] & 0xFF & 3) << 24);
            x.N1 = ((m[3] & 0xFF & ~3) >> 2) | ((m[4] & 0xFF) << 6) |
                   ((m[5] & 0xFF) << 14) | ((m[6] & 0xFF & 7) << 22);
            x.N2 = ((m[6] & 0xFF & ~7) >> 3) | ((m[7] & 0xFF) << 5) |
                   ((m[8] & 0xFF) << 13) | ((m[9] & 0xFF & 31) << 21);
            x.N3 = ((m[9] & 0xFF & ~31) >> 5) | ((m[10] & 0xFF) << 3) |
                   ((m[11] & 0xFF) << 11) | ((m[12] & 0xFF & 63) << 19);
            x.N4 = ((m[12] & 0xFF & ~63) >> 6) | ((m[13] & 0xFF) << 2) |
                   ((m[14] & 0xFF) << 10) | ((m[15] & 0xFF) << 18);
            x.N5 = (m[16] & 0xFF) | ((m[17] & 0xFF) << 8) |
                   ((m[18] & 0xFF) << 16) | ((m[19] & 0xFF & 1) << 24);
            x.N6 = ((m[19] & 0xFF & ~1) >> 1) | ((m[20] & 0xFF) << 7) |
                   ((m[21] & 0xFF) << 15) | ((m[22] & 0xFF & 7) << 23);
            x.N7 = ((m[22] & 0xFF & ~7) >> 3) | ((m[23] & 0xFF) << 5) |
                   ((m[24] & 0xFF) << 13) | ((m[25] & 0xFF & 15) << 21);
            x.N8 = ((m[25] & 0xFF & ~15) >> 4) | ((m[26] & 0xFF) << 4) |
                   ((m[27] & 0xFF) << 12) | ((m[28] & 0xFF & 63) << 20);
            x.N9 = ((m[28] & 0xFF & ~63) >> 6) | ((m[29] & 0xFF) << 2) |
                   ((m[30] & 0xFF) << 10) | ((m[31] & 0xFF) << 18);
        }

        /// <summary>
        ///     Check if reduced-form input >= 2^255-19
        /// </summary>
        private static bool IsOverflow(Long10 x)
        {
            return (x.N0 > P26 - 19) &
                ((x.N1 & x.N3 & x.N5 & x.N7 & x.N9) == P25) &
                ((x.N2 & x.N4 & x.N6 & x.N8) == P26) || x.N9 > P25;
        }

        /* Convert from internal format to little-endian byte format.  The
         * number must be in a reduced form which is output by the following ops:
         *     unpack, mul, sqr
         *     set --  if input in range 0 .. P25
         * If you're unsure if the number is reduced, first multiply it by 1.  */

        private static void Pack(Long10 x, byte[] m)
        {
            var ld = (IsOverflow(x) ? 1 : 0) - (x.N9 < 0 ? 1 : 0);
            var ud = ld * -(P25 + 1);
            ld *= 19;
            var t = ld + x.N0 + (x.N1 << 26);
            m[0] = (byte)t;
            m[1] = (byte)(t >> 8);
            m[2] = (byte)(t >> 16);
            m[3] = (byte)(t >> 24);
            t = (t >> 32) + (x.N2 << 19);
            m[4] = (byte)t;
            m[5] = (byte)(t >> 8);
            m[6] = (byte)(t >> 16);
            m[7] = (byte)(t >> 24);
            t = (t >> 32) + (x.N3 << 13);
            m[8] = (byte)t;
            m[9] = (byte)(t >> 8);
            m[10] = (byte)(t >> 16);
            m[11] = (byte)(t >> 24);
            t = (t >> 32) + (x.N4 << 6);
            m[12] = (byte)t;
            m[13] = (byte)(t >> 8);
            m[14] = (byte)(t >> 16);
            m[15] = (byte)(t >> 24);
            t = (t >> 32) + x.N5 + (x.N6 << 25);
            m[16] = (byte)t;
            m[17] = (byte)(t >> 8);
            m[18] = (byte)(t >> 16);
            m[19] = (byte)(t >> 24);
            t = (t >> 32) + (x.N7 << 19);
            m[20] = (byte)t;
            m[21] = (byte)(t >> 8);
            m[22] = (byte)(t >> 16);
            m[23] = (byte)(t >> 24);
            t = (t >> 32) + (x.N8 << 12);
            m[24] = (byte)t;
            m[25] = (byte)(t >> 8);
            m[26] = (byte)(t >> 16);
            m[27] = (byte)(t >> 24);
            t = (t >> 32) + ((x.N9 + ud) << 6);
            m[28] = (byte)t;
            m[29] = (byte)(t >> 8);
            m[30] = (byte)(t >> 16);
            m[31] = (byte)(t >> 24);
        }

        /// <summary>
        ///     Copy a number
        /// </summary>
        private static void Copy(Long10 numOut, Long10 numIn)
        {
            numOut.N0 = numIn.N0;
            numOut.N1 = numIn.N1;
            numOut.N2 = numIn.N2;
            numOut.N3 = numIn.N3;
            numOut.N4 = numIn.N4;
            numOut.N5 = numIn.N5;
            numOut.N6 = numIn.N6;
            numOut.N7 = numIn.N7;
            numOut.N8 = numIn.N8;
            numOut.N9 = numIn.N9;
        }

        /// <summary>
        ///     Set a number to value, which must be in range -185861411 .. 185861411
        /// </summary>
        private static void Set(Long10 numOut, int numIn)
        {
            numOut.N0 = numIn;
            numOut.N1 = 0;
            numOut.N2 = 0;
            numOut.N3 = 0;
            numOut.N4 = 0;
            numOut.N5 = 0;
            numOut.N6 = 0;
            numOut.N7 = 0;
            numOut.N8 = 0;
            numOut.N9 = 0;
        }

        /* Add/subtract two numbers.  The inputs must be in reduced form, and the
         * output isn't, so to do another addition or subtraction on the output,
         * first multiply it by one to reduce it. */
        private static void Add(Long10 xy, Long10 x, Long10 y)
        {
            xy.N0 = x.N0 + y.N0;
            xy.N1 = x.N1 + y.N1;
            xy.N2 = x.N2 + y.N2;
            xy.N3 = x.N3 + y.N3;
            xy.N4 = x.N4 + y.N4;
            xy.N5 = x.N5 + y.N5;
            xy.N6 = x.N6 + y.N6;
            xy.N7 = x.N7 + y.N7;
            xy.N8 = x.N8 + y.N8;
            xy.N9 = x.N9 + y.N9;
        }

        private static void Sub(Long10 xy, Long10 x, Long10 y)
        {
            xy.N0 = x.N0 - y.N0;
            xy.N1 = x.N1 - y.N1;
            xy.N2 = x.N2 - y.N2;
            xy.N3 = x.N3 - y.N3;
            xy.N4 = x.N4 - y.N4;
            xy.N5 = x.N5 - y.N5;
            xy.N6 = x.N6 - y.N6;
            xy.N7 = x.N7 - y.N7;
            xy.N8 = x.N8 - y.N8;
            xy.N9 = x.N9 - y.N9;
        }

        /// <summary>
        ///     Multiply a number by a small integer in range -185861411 .. 185861411.
        ///     The output is in reduced form, the input x need not be.  x and xy may point
        ///     to the same buffer.
        /// </summary>
        private static void MulSmall(Long10 xy, Long10 x, long y)
        {
            var temp = x.N8 * y;
            xy.N8 = temp & ((1 << 26) - 1);
            temp = (temp >> 26) + x.N9 * y;
            xy.N9 = temp & ((1 << 25) - 1);
            temp = 19 * (temp >> 25) + x.N0 * y;
            xy.N0 = temp & ((1 << 26) - 1);
            temp = (temp >> 26) + x.N1 * y;
            xy.N1 = temp & ((1 << 25) - 1);
            temp = (temp >> 25) + x.N2 * y;
            xy.N2 = temp & ((1 << 26) - 1);
            temp = (temp >> 26) + x.N3 * y;
            xy.N3 = temp & ((1 << 25) - 1);
            temp = (temp >> 25) + x.N4 * y;
            xy.N4 = temp & ((1 << 26) - 1);
            temp = (temp >> 26) + x.N5 * y;
            xy.N5 = temp & ((1 << 25) - 1);
            temp = (temp >> 25) + x.N6 * y;
            xy.N6 = temp & ((1 << 26) - 1);
            temp = (temp >> 26) + x.N7 * y;
            xy.N7 = temp & ((1 << 25) - 1);
            temp = (temp >> 25) + xy.N8;
            xy.N8 = temp & ((1 << 26) - 1);
            xy.N9 += temp >> 26;
        }

        /// <summary>
        ///     Multiply two numbers. The output is in reduced form, the inputs need not be.
        /// </summary>
        private static void Multiply(Long10 xy, Long10 x, Long10 y)
        {
            /* sahn0:
             * Using local variables to avoid class access.
             * This seem to improve performance a bit...
             */
            long
                x0 = x.N0,
                x1 = x.N1,
                x2 = x.N2,
                x3 = x.N3,
                x4 = x.N4,
                x5 = x.N5,
                x6 = x.N6,
                x7 = x.N7,
                x8 = x.N8,
                x9 = x.N9;
            long
                y0 = y.N0,
                y1 = y.N1,
                y2 = y.N2,
                y3 = y.N3,
                y4 = y.N4,
                y5 = y.N5,
                y6 = y.N6,
                y7 = y.N7,
                y8 = y.N8,
                y9 = y.N9;
            var
                t = x0 * y8 + x2 * y6 + x4 * y4 + x6 * y2 +
                    x8 * y0 + 2 * (x1 * y7 + x3 * y5 +
                                   x5 * y3 + x7 * y1) + 38 *
                    x9 * y9;
            xy.N8 = t & ((1 << 26) - 1);
            t = (t >> 26) + x0 * y9 + x1 * y8 + x2 * y7 +
                x3 * y6 + x4 * y5 + x5 * y4 +
                x6 * y3 + x7 * y2 + x8 * y1 +
                x9 * y0;
            xy.N9 = t & ((1 << 25) - 1);
            t = x0 * y0 + 19 * ((t >> 25) + x2 * y8 + x4 * y6
                                + x6 * y4 + x8 * y2) + 38 *
                (x1 * y9 + x3 * y7 + x5 * y5 +
                 x7 * y3 + x9 * y1);
            xy.N0 = t & ((1 << 26) - 1);
            t = (t >> 26) + x0 * y1 + x1 * y0 + 19 * (x2 * y9
                                                      + x3 * y8 + x4 * y7 + x5 * y6 +
                                                      x6 * y5 + x7 * y4 + x8 * y3 +
                                                      x9 * y2);
            xy.N1 = t & ((1 << 25) - 1);
            t = (t >> 25) + x0 * y2 + x2 * y0 + 19 * (x4 * y8
                                                      + x6 * y6 + x8 * y4) + 2 * x1 * y1
                + 38 * (x3 * y9 + x5 * y7 +
                        x7 * y5 + x9 * y3);
            xy.N2 = t & ((1 << 26) - 1);
            t = (t >> 26) + x0 * y3 + x1 * y2 + x2 * y1 +
                x3 * y0 + 19 * (x4 * y9 + x5 * y8 +
                                x6 * y7 + x7 * y6 +
                                x8 * y5 + x9 * y4);
            xy.N3 = t & ((1 << 25) - 1);
            t = (t >> 25) + x0 * y4 + x2 * y2 + x4 * y0 + 19 *
                (x6 * y8 + x8 * y6) + 2 * (x1 * y3 +
                                           x3 * y1) + 38 *
                (x5 * y9 + x7 * y7 + x9 * y5);
            xy.N4 = t & ((1 << 26) - 1);
            t = (t >> 26) + x0 * y5 + x1 * y4 + x2 * y3 +
                x3 * y2 + x4 * y1 + x5 * y0 + 19 *
                (x6 * y9 + x7 * y8 + x8 * y7 +
                 x9 * y6);
            xy.N5 = t & ((1 << 25) - 1);
            t = (t >> 25) + x0 * y6 + x2 * y4 + x4 * y2 +
                x6 * y0 + 19 * x8 * y8 + 2 * (x1 * y5 +
                                              x3 * y3 + x5 * y1) + 38 *
                (x7 * y9 + x9 * y7);
            xy.N6 = t & ((1 << 26) - 1);
            t = (t >> 26) + x0 * y7 + x1 * y6 + x2 * y5 +
                x3 * y4 + x4 * y3 + x5 * y2 +
                x6 * y1 + x7 * y0 + 19 * (x8 * y9 +
                                          x9 * y8);
            xy.N7 = t & ((1 << 25) - 1);
            t = (t >> 25) + xy.N8;
            xy.N8 = t & ((1 << 26) - 1);
            xy.N9 += t >> 26;
        }

        /// <summary>
        ///     Square a number.  Optimization of  Multiply(x2, x, x)
        /// </summary>
        private static void Square(Long10 xsqr, Long10 x)
        {
            long
                x0 = x.N0,
                x1 = x.N1,
                x2 = x.N2,
                x3 = x.N3,
                x4 = x.N4,
                x5 = x.N5,
                x6 = x.N6,
                x7 = x.N7,
                x8 = x.N8,
                x9 = x.N9;

            var t = x4 * x4 + 2 * (x0 * x8 + x2 * x6) + 38 *
                x9 * x9 + 4 * (x1 * x7 + x3 * x5);

            xsqr.N8 = t & ((1 << 26) - 1);
            t = (t >> 26) + 2 * (x0 * x9 + x1 * x8 + x2 * x7 +
                                 x3 * x6 + x4 * x5);
            xsqr.N9 = t & ((1 << 25) - 1);
            t = 19 * (t >> 25) + x0 * x0 + 38 * (x2 * x8 +
                                                 x4 * x6 + x5 * x5) + 76 * (x1 * x9
                                                                            + x3 * x7);
            xsqr.N0 = t & ((1 << 26) - 1);
            t = (t >> 26) + 2 * x0 * x1 + 38 * (x2 * x9 +
                                                x3 * x8 + x4 * x7 + x5 * x6);
            xsqr.N1 = t & ((1 << 25) - 1);
            t = (t >> 25) + 19 * x6 * x6 + 2 * (x0 * x2 +
                                                x1 * x1) + 38 * x4 * x8 + 76 *
                (x3 * x9 + x5 * x7);
            xsqr.N2 = t & ((1 << 26) - 1);
            t = (t >> 26) + 2 * (x0 * x3 + x1 * x2) + 38 *
                (x4 * x9 + x5 * x8 + x6 * x7);
            xsqr.N3 = t & ((1 << 25) - 1);
            t = (t >> 25) + x2 * x2 + 2 * x0 * x4 + 38 *
                (x6 * x8 + x7 * x7) + 4 * x1 * x3 + 76 *
                x5 * x9;
            xsqr.N4 = t & ((1 << 26) - 1);
            t = (t >> 26) + 2 * (x0 * x5 + x1 * x4 + x2 * x3)
                          + 38 * (x6 * x9 + x7 * x8);
            xsqr.N5 = t & ((1 << 25) - 1);
            t = (t >> 25) + 19 * x8 * x8 + 2 * (x0 * x6 +
                                                x2 * x4 + x3 * x3) + 4 * x1 * x5 +
                76 * x7 * x9;
            xsqr.N6 = t & ((1 << 26) - 1);
            t = (t >> 26) + 2 * (x0 * x7 + x1 * x6 + x2 * x5 +
                                 x3 * x4) + 38 * x8 * x9;
            xsqr.N7 = t & ((1 << 25) - 1);
            t = (t >> 25) + xsqr.N8;
            xsqr.N8 = t & ((1 << 26) - 1);
            xsqr.N9 += t >> 26;
        }

        /// <summary>
        ///     Calculates a reciprocal.  The output is in reduced form, the inputs need not
        ///     be.  Simply calculates  y = x^(p-2)  so it's not too fast. */
        ///     When sqrtassist is true, it instead calculates y = x^((p-5)/8)
        /// </summary>
        private static void Reciprocal(Long10 y, Long10 x, bool sqrtAssist)
        {
            Long10
                t0 = new Long10(),
                t1 = new Long10(),
                t2 = new Long10(),
                t3 = new Long10(),
                t4 = new Long10();
            int i;
            /* the chain for x^(2^255-21) is straight from djb's implementation */
            Square(t1, x); /*  2 == 2 * 1	*/
            Square(t2, t1); /*  4 == 2 * 2	*/
            Square(t0, t2); /*  8 == 2 * 4	*/
            Multiply(t2, t0, x); /*  9 == 8 + 1	*/
            Multiply(t0, t2, t1); /* 11 == 9 + 2	*/
            Square(t1, t0); /* 22 == 2 * 11	*/
            Multiply(t3, t1, t2); /* 31 == 22 + 9
                    == 2^5   - 2^0	*/
            Square(t1, t3); /* 2^6   - 2^1	*/
            Square(t2, t1); /* 2^7   - 2^2	*/
            Square(t1, t2); /* 2^8   - 2^3	*/
            Square(t2, t1); /* 2^9   - 2^4	*/
            Square(t1, t2); /* 2^10  - 2^5	*/
            Multiply(t2, t1, t3); /* 2^10  - 2^0	*/
            Square(t1, t2); /* 2^11  - 2^1	*/
            Square(t3, t1); /* 2^12  - 2^2	*/
            for (i = 1; i < 5; i++)
            {
                Square(t1, t3);
                Square(t3, t1);
            } /* t3 */ /* 2^20  - 2^10	*/

            Multiply(t1, t3, t2); /* 2^20  - 2^0	*/
            Square(t3, t1); /* 2^21  - 2^1	*/
            Square(t4, t3); /* 2^22  - 2^2	*/
            for (i = 1; i < 10; i++)
            {
                Square(t3, t4);
                Square(t4, t3);
            } /* t4 */ /* 2^40  - 2^20	*/

            Multiply(t3, t4, t1); /* 2^40  - 2^0	*/
            for (i = 0; i < 5; i++)
            {
                Square(t1, t3);
                Square(t3, t1);
            } /* t3 */ /* 2^50  - 2^10	*/

            Multiply(t1, t3, t2); /* 2^50  - 2^0	*/
            Square(t2, t1); /* 2^51  - 2^1	*/
            Square(t3, t2); /* 2^52  - 2^2	*/
            for (i = 1; i < 25; i++)
            {
                Square(t2, t3);
                Square(t3, t2);
            } /* t3 */ /* 2^100 - 2^50 */

            Multiply(t2, t3, t1); /* 2^100 - 2^0	*/
            Square(t3, t2); /* 2^101 - 2^1	*/
            Square(t4, t3); /* 2^102 - 2^2	*/
            for (i = 1; i < 50; i++)
            {
                Square(t3, t4);
                Square(t4, t3);
            } /* t4 */ /* 2^200 - 2^100 */

            Multiply(t3, t4, t2); /* 2^200 - 2^0	*/
            for (i = 0; i < 25; i++)
            {
                Square(t4, t3);
                Square(t3, t4);
            } /* t3 */ /* 2^250 - 2^50	*/

            Multiply(t2, t3, t1); /* 2^250 - 2^0	*/
            Square(t1, t2); /* 2^251 - 2^1	*/
            Square(t2, t1); /* 2^252 - 2^2	*/
            if (sqrtAssist)
            {
                Multiply(y, x, t2); /* 2^252 - 3 */
            }
            else
            {
                Square(t1, t2); /* 2^253 - 2^3	*/
                Square(t2, t1); /* 2^254 - 2^4	*/
                Square(t1, t2); /* 2^255 - 2^5	*/
                Multiply(y, t1, t0); /* 2^255 - 21	*/
            }
        }

        /// <summary>
        ///     Checks if x is "negative", requires reduced input
        /// </summary>
        /// <param name="x">must be reduced input</param>
        private static int IsNegative(Long10 x)
        {
            return (int)((IsOverflow(x) | (x.N9 < 0) ? 1 : 0) ^ (x.N0 & 1));
        }

        /********************* Elliptic curve *********************/

        /* y^2 = x^3 + 486662 x^2 + x  over GF(2^255-19) */

        /* t1 = ax + az
         * t2 = ax - az  */

        private static void MontyPrepare(Long10 t1, Long10 t2, Long10 ax, Long10 az)
        {
            Add(t1, ax, az);
            Sub(t2, ax, az);
        }

        /* A = P + Q   where
         *  X(A) = ax/az
         *  X(P) = (t1+t2)/(t1-t2)
         *  X(Q) = (t3+t4)/(t3-t4)
         *  X(P-Q) = dx
         * clobbers t1 and t2, preserves t3 and t4  */

        private static void MontyAdd(Long10 t1, Long10 t2, Long10 t3, Long10 t4, Long10 ax, Long10 az, Long10 dx)
        {
            Multiply(ax, t2, t3);
            Multiply(az, t1, t4);
            Add(t1, ax, az);
            Sub(t2, ax, az);
            Square(ax, t1);
            Square(t1, t2);
            Multiply(az, t1, dx);
        }

        /* B = 2 * Q   where
         *  X(B) = bx/bz
         *  X(Q) = (t3+t4)/(t3-t4)
         * clobbers t1 and t2, preserves t3 and t4  */

        private static void MontyDouble(Long10 t1, Long10 t2, Long10 t3, Long10 t4, Long10 bx, Long10 bz)
        {
            Square(t1, t3);
            Square(t2, t4);
            Multiply(bx, t1, t2);
            Sub(t2, t1, t2);
            MulSmall(bz, t2, 121665);
            Add(t1, t1, bz);
            Multiply(bz, t1, t2);
        }

        /// <summary>
        ///     Y^2 = X^3 + 486662 X^2 + X
        /// </summary>
        /// <param name="y2">output</param>
        /// <param name="x">X</param>
        /// <param name="temp">temporary</param>
        private static void CurveEquationInline(Long10 y2, Long10 x, Long10 temp)
        {
            Square(temp, x);
            MulSmall(y2, x, 486662);
            Add(temp, temp, y2);
            temp.N0++;
            Multiply(y2, temp, x);
        }

        /// <summary>
        ///     P = kG   and  s = sign(P)/k
        /// </summary>
        private static void Core(byte[] publicKey, byte[] signingKey, byte[] privateKey, byte[] peerPublicKey)
        {
            if (publicKey == null) throw new ArgumentNullException(nameof(publicKey));
            if (publicKey.Length != 32)
                throw new ArgumentException(
                    $@"publicKey must be 32 bytes long (but was {publicKey.Length} bytes long)",
                    nameof(publicKey));

            if (signingKey != null && signingKey.Length != 32)
                throw new ArgumentException(
                    $@"signingKey must be null or 32 bytes long (but was {signingKey.Length} bytes long)",
                    nameof(signingKey));

            if (privateKey == null) throw new ArgumentNullException(nameof(privateKey));
            if (privateKey.Length != 32)
                throw new ArgumentException(
                    $@"privateKey must be 32 bytes long (but was {privateKey.Length} bytes long)",
                    nameof(privateKey));

            if (peerPublicKey != null && peerPublicKey.Length != 32)
                throw new ArgumentException(
                    $@"peerPublicKey must be null or 32 bytes long (but was {peerPublicKey.Length} bytes long)",
                    nameof(peerPublicKey));

            Long10
                dx = new Long10(),
                t1 = new Long10(),
                t2 = new Long10(),
                t3 = new Long10(),
                t4 = new Long10();
            Long10[]
                x = { new Long10(), new Long10() },
                z = { new Long10(), new Long10() };

            /* unpack the base */
            if (peerPublicKey != null)
                Unpack(dx, peerPublicKey);
            else
                Set(dx, 9);

            /* 0G = point-at-infinity */
            Set(x[0], 1);
            Set(z[0], 0);

            /* 1G = G */
            Copy(x[1], dx);
            Set(z[1], 1);

            for (var i = 32; i-- != 0;)
                for (var j = 8; j-- != 0;)
                {
                    /* swap arguments depending on bit */
                    var bit1 = ((privateKey[i] & 0xFF) >> j) & 1;
                    var bit0 = (~(privateKey[i] & 0xFF) >> j) & 1;
                    var ax = x[bit0];
                    var az = z[bit0];
                    var bx = x[bit1];
                    var bz = z[bit1];

                    /* a' = a + b	*/
                    /* b' = 2 b	*/
                    MontyPrepare(t1, t2, ax, az);
                    MontyPrepare(t3, t4, bx, bz);
                    MontyAdd(t1, t2, t3, t4, ax, az, dx);
                    MontyDouble(t1, t2, t3, t4, bx, bz);
                }

            Reciprocal(t1, z[0], false);
            Multiply(dx, x[0], t1);
            Pack(dx, publicKey);

            /* calculate s such that s abs(P) = G  .. assumes G is std base point */
            if (signingKey != null)
            {
                CurveEquationInline(t1, dx, t2); /* t1 = Py^2  */
                Reciprocal(t3, z[1], false); /* where Q=P+G ... */
                Multiply(t2, x[1], t3); /* t2 = Qx  */
                Add(t2, t2, dx); /* t2 = Qx + Px  */
                t2.N0 += 9 + 486662; /* t2 = Qx + Px + Gx + 486662  */
                dx.N0 -= 9; /* dx = Px - Gx  */
                Square(t3, dx); /* t3 = (Px - Gx)^2  */
                Multiply(dx, t2, t3); /* dx = t2 (Px - Gx)^2  */
                Sub(dx, dx, t1); /* dx = t2 (Px - Gx)^2 - Py^2  */
                dx.N0 -= 39420360; /* dx = t2 (Px - Gx)^2 - Py^2 - Gy^2  */
                Multiply(t1, dx, BaseR2Y); /* t1 = -Py  */
                if (IsNegative(t1) != 0) /* sign is 1, so just copy  */
                    Copy32(privateKey, signingKey);
                else /* sign is -1, so negate  */
                    MultiplyArraySmall(signingKey, OrderTimes8, 0, privateKey, 32, -1);

                /* reduce s mod q
                 * (is this needed?  do it just in case, it's fast anyway) */
                //divmod((dstptr) t1, s, 32, order25519, 32);

                /* take reciprocal of s mod q */
                var temp1 = new byte[32];
                var temp2 = new byte[64];
                var temp3 = new byte[64];
                Copy32(Order, temp1);
                Copy32(Egcd32(temp2, temp3, signingKey, temp1), signingKey);
                if ((signingKey[31] & 0x80) != 0)
                    MultiplyArraySmall(signingKey, signingKey, 0, Order, 32, 1);
            }
        }

        /////////////////////////////////////////////////////////////////////////// 

        /* sahn0:
         * Using this class instead of long[10] to avoid bounds checks. */

        private sealed class Long10
        {
            public long N0, N1, N2, N3, N4, N5, N6, N7, N8, N9;

            public Long10()
            {
            }

            public Long10(
                long n0, long n1, long n2, long n3, long n4,
                long n5, long n6, long n7, long n8, long n9)
            {
                N0 = n0;
                N1 = n1;
                N2 = n2;
                N3 = n3;
                N4 = n4;
                N5 = n5;
                N6 = n6;
                N7 = n7;
                N8 = n8;
                N9 = n9;
            }
        }
    }
}
