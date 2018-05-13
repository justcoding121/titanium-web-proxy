/*
 * Copyright 2014 Twitter, Inc
 * This file is a derivative work modified by Ringo Leese
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.IO;

namespace Titanium.Web.Proxy.Http2.Hpack
{
    public class HuffmanEncoder
    {
        /// <summary>
        /// Huffman Encoder
        /// </summary>
        public static readonly HuffmanEncoder Instance = new HuffmanEncoder();

        /// <summary>
        /// the Huffman codes indexed by symbol
        /// </summary>
        private readonly int[] codes = HpackUtil.HuffmanCodes;

        /// <summary>
        /// the length of each Huffman code
        /// </summary>
        private readonly byte[] lengths = HpackUtil.HuffmanCodeLengths;

        /// <summary>
        /// Compresses the input string literal using the Huffman coding.
        /// </summary>
        /// <param name="output">the output stream for the compressed data</param>
        /// <param name="data">the string literal to be Huffman encoded</param>
        /// <exception cref="IOException">if an I/O error occurs.</exception>
        /// <see cref="Encode(BinaryWriter,byte[],int,int)"/>
        public void Encode(BinaryWriter output, byte[] data)
        {
            Encode(output, data, 0, data.Length);
        }

        /// <summary>
        /// Compresses the input string literal using the Huffman coding.
        /// </summary>
        /// <param name="output">the output stream for the compressed data</param>
        /// <param name="data">the string literal to be Huffman encoded</param>
        /// <param name="off">the start offset in the data</param>
        /// <param name="len">the number of bytes to encode</param>
        /// <exception cref="IOException">if an I/O error occurs. In particular, an <code>IOException</code> may be thrown if the output stream has been closed.</exception>
        public void Encode(BinaryWriter output, byte[] data, int off, int len)
        {
            if (output == null)
            {
                throw new ArgumentNullException(nameof(output));
            }

            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            if (off < 0 || len < 0 || (off + len) < 0 || off > data.Length || (off + len) > data.Length)
            {
                throw new IndexOutOfRangeException();
            }

            if (len == 0)
            {
                return;
            }

            long current = 0L;
            int n = 0;

            for (int i = 0; i < len; i++)
            {
                int b = data[off + i] & 0xFF;
                uint code = (uint)codes[b];
                int nbits = lengths[b];

                current <<= nbits;
                current |= code;
                n += nbits;

                while (n >= 8)
                {
                    n -= 8;
                    output.Write(((byte)(current >> n)));
                }
            }

            if (n > 0)
            {
                current <<= (8 - n);
                current |= (uint)(0xFF >> n); // this should be EOS symbol
                output.Write((byte)current);
            }
        }

        /// <summary>
        /// Returns the number of bytes required to Huffman encode the input string literal.
        /// </summary>
        /// <returns>the number of bytes required to Huffman encode <code>data</code></returns>
        /// <param name="data">the string literal to be Huffman encoded</param>
        public int GetEncodedLength(byte[] data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            long len = 0L;
            foreach (byte b in data)
            {
                len += lengths[b];
            }

            return (int)((len + 7) >> 3);
        }
    }
}
