using System;
using System.Security.Cryptography;
using System.Text;

namespace XmppAsync
{
    class Utils
    {
        internal static string ComputeSHA1Hash(string data, Encoding enc)
        {
            return ComputeSHA1Hash(enc.GetBytes(data));
        }

        internal static string ComputeSHA1Hash(byte[] bytes)
        {
            return ComputeSHA1Hash(bytes, bytes.Length);
        }

        internal static string ComputeSHA1Hash(byte[] bytes, int iLen)
        {
            iLen = Math.Min(iLen, bytes.Length);

            using (var alg = SHA1.Create())
            {
                return ByteArrayToHexString(alg.ComputeHash(bytes, 0, iLen));
            }
        }

        internal static string ByteArrayToHexString(byte[] bytes)
        {
            StringBuilder builder = new StringBuilder(3 * bytes.Length);
            for (int i = 0; i < bytes.Length; i++)
            {
                builder.AppendFormat("{0:x2}", bytes[i]);
            }
            return builder.ToString().ToLowerInvariant();
        }
    }
}
