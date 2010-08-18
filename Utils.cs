#region Copyright 2010 Boris Byk.
/*
 * Copyright 2010 Boris Byk.
 *
 * Licensed under the Apache License, Version 2.0 (the "License"); you may
 * not use this file except in compliance with the License. You may obtain
 * a copy of the License at
 *
 *  http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS, WITHOUT
 * WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the
 * License for the specific language governing permissions and limitations
 * under the License.
 */
#endregion

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
