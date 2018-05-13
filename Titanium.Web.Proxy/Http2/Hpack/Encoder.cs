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
using System.Text;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Http2.Hpack
{
    public class Encoder
    {
        private const int bucketSize = 17;

        // a linked hash map of header fields
        private readonly HeaderEntry[] headerFields = new HeaderEntry[bucketSize];
        private readonly HeaderEntry head = new HeaderEntry(-1, string.Empty, string.Empty, int.MaxValue, null);
        private int size;

        /// <summary>
        /// Gets the the maximum table size.
        /// </summary>
        /// <value>
        /// The max header table size.
        /// </value>
        public int MaxHeaderTableSize { get; private set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="Encoder"/> class.
        /// </summary>
        /// <param name="maxHeaderTableSize">Max header table size.</param>
        public Encoder(int maxHeaderTableSize)
        {
            if (maxHeaderTableSize < 0)
            {
                throw new ArgumentException("Illegal Capacity: " + maxHeaderTableSize);
            }

            MaxHeaderTableSize = maxHeaderTableSize;
            head.Before = head.After = head;
        }

        /// <summary>
        /// Encode the header field into the header block.
        /// </summary>
        /// <param name="output">Output.</param>
        /// <param name="name">Name.</param>
        /// <param name="value">Value.</param>
        /// <param name="sensitive">If set to <c>true</c> sensitive.</param>
        public void EncodeHeader(BinaryWriter output, string name, string value, bool sensitive = false)
        {
            // If the header value is sensitive then it must never be indexed
            if (sensitive)
            {
                int nameIndex = GetNameIndex(name);
                EncodeLiteral(output, name, value, HpackUtil.IndexType.Never, nameIndex);
                return;
            }

            // If the peer will only use the static table
            if (MaxHeaderTableSize == 0)
            {
                int staticTableIndex = StaticTable.GetIndex(name, value);
                if (staticTableIndex == -1)
                {
                    int nameIndex = StaticTable.GetIndex(name);
                    EncodeLiteral(output, name, value, HpackUtil.IndexType.None, nameIndex);
                }
                else
                {
                    EncodeInteger(output, 0x80, 7, staticTableIndex);
                }

                return;
            }

            int headerSize = HttpHeader.SizeOf(name, value);

            // If the headerSize is greater than the max table size then it must be encoded literally
            if (headerSize > MaxHeaderTableSize)
            {
                int nameIndex = GetNameIndex(name);
                EncodeLiteral(output, name, value, HpackUtil.IndexType.None, nameIndex);
                return;
            }

            var headerField = GetEntry(name, value);
            if (headerField != null)
            {
                int index = GetIndex(headerField.Index) + StaticTable.Length;
                
                // Section 6.1. Indexed Header Field Representation
                EncodeInteger(output, 0x80, 7, index);
            }
            else
            {
                int staticTableIndex = StaticTable.GetIndex(name, value);
                if (staticTableIndex != -1)
                {
                    // Section 6.1. Indexed Header Field Representation
                    EncodeInteger(output, 0x80, 7, staticTableIndex);
                }
                else
                {
                    int nameIndex = GetNameIndex(name);
                    EnsureCapacity(headerSize);

                    var indexType = HpackUtil.IndexType.Incremental;
                    EncodeLiteral(output, name, value, indexType, nameIndex);
                    Add(name, value);
                }
            }
        }

        /// <summary>
        /// Set the maximum table size.
        /// </summary>
        /// <param name="output">Output.</param>
        /// <param name="maxHeaderTableSize">Max header table size.</param>
        public void SetMaxHeaderTableSize(BinaryWriter output, int maxHeaderTableSize)
        {
            if (maxHeaderTableSize < 0)
            {
                throw new ArgumentException("Illegal Capacity", nameof(maxHeaderTableSize));
            }

            if (MaxHeaderTableSize == maxHeaderTableSize)
            {
                return;
            }

            MaxHeaderTableSize = maxHeaderTableSize;
            EnsureCapacity(0);
            EncodeInteger(output, 0x20, 5, maxHeaderTableSize);
        }

        /// <summary>
        /// Encode integer according to Section 5.1.
        /// </summary>
        /// <param name="output">Output.</param>
        /// <param name="mask">Mask.</param>
        /// <param name="n">N.</param>
        /// <param name="i">The index.</param>
        private static void EncodeInteger(BinaryWriter output, int mask, int n, int i)
        {
            if (n < 0 || n > 8)
            {
                throw new ArgumentException("N: " + n);
            }

            int nbits = 0xFF >> (8 - n);
            if (i < nbits)
            {
                output.Write((byte)(mask | i));
            }
            else
            {
                output.Write((byte)(mask | nbits));
                int length = i - nbits;
                while (true)
                {
                    if ((length & ~0x7F) == 0)
                    {
                        output.Write((byte)length);
                        return;
                    }

                    output.Write((byte)((length & 0x7F) | 0x80));
                    length >>= 7;
                }
            }
        }

        /// <summary>
        /// Encode string literal according to Section 5.2.
        /// </summary>
        /// <param name="output">Output.</param>
        /// <param name="stringLiteral">String literal.</param>
        private void EncodeStringLiteral(BinaryWriter output, string stringLiteral)
        {
            var stringData = Encoding.UTF8.GetBytes(stringLiteral);
            int huffmanLength = HuffmanEncoder.Instance.GetEncodedLength(stringData);
            if (huffmanLength < stringLiteral.Length)
            {
                EncodeInteger(output, 0x80, 7, huffmanLength);
                HuffmanEncoder.Instance.Encode(output, stringData);
            }
            else
            {
                EncodeInteger(output, 0x00, 7, stringData.Length);
                output.Write(stringData, 0, stringData.Length);
            }
        }

        /// <summary>
        /// Encode literal header field according to Section 6.2.
        /// </summary>
        /// <param name="output">Output.</param>
        /// <param name="name">Name.</param>
        /// <param name="value">Value.</param>
        /// <param name="indexType">Index type.</param>
        /// <param name="nameIndex">Name index.</param>
        private void EncodeLiteral(BinaryWriter output, string name, string value, HpackUtil.IndexType indexType,
            int nameIndex)
        {
            int mask;
            int prefixBits;
            switch (indexType)
            {
                case HpackUtil.IndexType.Incremental:
                    mask = 0x40;
                    prefixBits = 6;
                    break;

                case HpackUtil.IndexType.None:
                    mask = 0x00;
                    prefixBits = 4;
                    break;

                case HpackUtil.IndexType.Never:
                    mask = 0x10;
                    prefixBits = 4;
                    break;

                default:
                    throw new Exception("should not reach here");
            }

            EncodeInteger(output, mask, prefixBits, nameIndex == -1 ? 0 : nameIndex);
            if (nameIndex == -1)
            {
                EncodeStringLiteral(output, name);
            }

            EncodeStringLiteral(output, value);
        }

        private int GetNameIndex(string name)
        {
            int index = StaticTable.GetIndex(name);
            if (index == -1)
            {
                index = GetIndex(name);
                if (index >= 0)
                {
                    index += StaticTable.Length;
                }
            }

            return index;
        }

        /// <summary>
        /// Ensure that the dynamic table has enough room to hold 'headerSize' more bytes.
        /// Removes the oldest entry from the dynamic table until sufficient space is available.
        /// </summary>
        /// <param name="headerSize">Header size.</param>
        private void EnsureCapacity(int headerSize)
        {
            while (size + headerSize > MaxHeaderTableSize)
            {
                int index = Length();
                if (index == 0)
                {
                    break;
                }

                Remove();
            }
        }

        /// <summary>
        /// Return the number of header fields in the dynamic table.
        /// </summary>
        private int Length()
        {
            return size == 0 ? 0 : head.After.Index - head.Before.Index + 1;
        }

        /// <summary>
        /// Returns the header entry with the lowest index value for the header field.
        /// Returns null if header field is not in the dynamic table.
        /// </summary>
        /// <returns>The entry.</returns>
        /// <param name="name">Name.</param>
        /// <param name="value">Value.</param>
        private HeaderEntry GetEntry(string name, string value)
        {
            if (Length() == 0 || name == null || value == null)
            {
                return null;
            }

            int h = Hash(name);
            int i = Index(h);
            for (var e = headerFields[i]; e != null; e = e.Next)
            {
                if (e.Hash == h && Equals(name, e.Name) && Equals(value, e.Value))
                {
                    return e;
                }
            }

            return null;
        }

        /// <summary>
        /// Returns the lowest index value for the header field name in the dynamic table.
        /// Returns -1 if the header field name is not in the dynamic table.
        /// </summary>
        /// <returns>The index.</returns>
        /// <param name="name">Name.</param>
        private int GetIndex(string name)
        {
            if (Length() == 0 || name == null)
            {
                return -1;
            }

            int h = Hash(name);
            int i = Index(h);
            int index = -1;
            for (var e = headerFields[i]; e != null; e = e.Next)
            {
                if (e.Hash == h && HpackUtil.Equals(name, e.Name))
                {
                    index = e.Index;
                    break;
                }
            }

            return GetIndex(index);
        }

        /// <summary>
        /// Compute the index into the dynamic table given the index in the header entry.
        /// </summary>
        /// <returns>The index.</returns>
        /// <param name="index">Index.</param>
        private int GetIndex(int index)
        {
            if (index == -1)
            {
                return index;
            }

            return index - head.Before.Index + 1;
        }

        /// <summary>
        /// Add the header field to the dynamic table.
        /// Entries are evicted from the dynamic table until the size of the table
        /// and the new header field is less than the table's capacity.
        /// If the size of the new entry is larger than the table's capacity,
        /// the dynamic table will be cleared.
        /// </summary>
        /// <param name="name">Name.</param>
        /// <param name="value">Value.</param>
        private void Add(string name, string value)
        {
            int headerSize = HttpHeader.SizeOf(name, value);

            // Clear the table if the header field size is larger than the capacity.
            if (headerSize > MaxHeaderTableSize)
            {
                Clear();
                return;
            }

            // Evict oldest entries until we have enough capacity.
            while (size + headerSize > MaxHeaderTableSize)
            {
                Remove();
            }

            int h = Hash(name);
            int i = Index(h);
            var old = headerFields[i];
            var e = new HeaderEntry(h, name, value, head.Before.Index - 1, old);
            headerFields[i] = e;
            e.AddBefore(head);
            size += headerSize;
        }

        /// <summary>
        /// Remove and return the oldest header field from the dynamic table.
        /// </summary>
        private HttpHeader Remove()
        {
            if (size == 0)
            {
                return null;
            }

            var eldest = head.After;
            int h = eldest.Hash;
            int i = Index(h);
            var prev = headerFields[i];
            var e = prev;
            while (e != null)
            {
                var next = e.Next;
                if (e == eldest)
                {
                    if (prev == eldest)
                    {
                        headerFields[i] = next;
                    }
                    else
                    {
                        prev.Next = next;
                    }

                    eldest.Remove();
                    size -= eldest.Size;
                    return eldest;
                }

                prev = e;
                e = next;
            }

            return null;
        }

        /// <summary>
        /// Remove all entries from the dynamic table.
        /// </summary>
        private void Clear()
        {
            for (int i = 0; i < headerFields.Length; i++)
            {
                headerFields[i] = null;
            }

            head.Before = head.After = head;
            size = 0;
        }

        /// <summary>
        /// Returns the hash code for the given header field name.
        /// </summary>
        /// <returns><c>true</c> if hash name; otherwise, <c>false</c>.</returns>
        /// <param name="name">Name.</param>
        private static int Hash(string name)
        {
            int h = 0;
            for (int i = 0; i < name.Length; i++)
            {
                h = 31 * h + name[i];
            }

            if (h > 0)
            {
                return h;
            }

            if (h == int.MinValue)
            {
                return int.MaxValue;
            }

            return -h;
        }

        /// <summary>
        /// Returns the index into the hash table for the hash code h.
        /// </summary>
        /// <param name="h">The height.</param>
        private static int Index(int h)
        {
            return h % bucketSize;
        }

        /// <summary>
        /// A linked hash map HeaderField entry.
        /// </summary>
        private class HeaderEntry : HttpHeader
        {
            // This is used to compute the index in the dynamic table.

            // These properties comprise the doubly linked list used for iteration.
            public HeaderEntry Before { get; set; }

            public HeaderEntry After { get; set; }

            // These fields comprise the chained list for header fields with the same hash.
            public HeaderEntry Next { get; set; }

            public int Hash { get; }

            public int Index { get; }

            /// <summary>
            /// Creates new entry.
            /// </summary>
            /// <param name="hash">Hash.</param>
            /// <param name="name">Name.</param>
            /// <param name="value">Value.</param>
            /// <param name="index">Index.</param>
            /// <param name="next">Next.</param>
            public HeaderEntry(int hash, string name, string value, int index, HeaderEntry next) : base(name, value)
            {
                Index = index;
                Hash = hash;
                Next = next;
            }

            /// <summary>
            /// Removes this entry from the linked list.
            /// </summary>
            public void Remove()
            {
                Before.After = After;
                After.Before = Before;
            }

            /// <summary>
            /// Inserts this entry before the specified existing entry in the list.
            /// </summary>
            /// <param name="existingEntry">Existing entry.</param>
            public void AddBefore(HeaderEntry existingEntry)
            {
                After = existingEntry;
                Before = existingEntry.Before;
                Before.After = this;
                After.Before = this;
            }
        }
    }
}
