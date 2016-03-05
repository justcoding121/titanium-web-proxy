using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Titanium.Web.Proxy.Helpers
{
    /// <summary>
    /// A custom binary reader that would allo us to read string line by line
    /// using the specified encoding
    /// as well as to read bytes as required
    /// </summary>
    public class CustomBinaryReader : BinaryReader
    {
        internal CustomBinaryReader(Stream stream, Encoding encoding)
            : base(stream, encoding)
        {
        }

        /// <summary>
        /// Read a line from the byte stream
        /// </summary>
        /// <returns></returns>
        internal string ReadLine()
        {
            var readBuffer = new StringBuilder();

            try
            {
                var lastChar = default(char);
                var buffer = new char[1];

                while (Read(buffer, 0, 1) > 0)
                {
                    if (lastChar == '\r' && buffer[0] == '\n')
                    {
                        return readBuffer.Remove(readBuffer.Length - 1, 1).ToString();
                    }
                    if (buffer[0] == '\0')
                    {
                        return readBuffer.ToString();
                    }
                    readBuffer.Append(buffer);
                    lastChar = buffer[0];
                }

                return readBuffer.ToString();
            }
            catch (IOException)
            {
                return readBuffer.ToString();
            }
        }

        /// <summary>
        /// Read until the last new line
        /// </summary>
        /// <returns></returns>
        internal List<string> ReadAllLines()
        {
            string tmpLine;
            var requestLines = new List<string>();
            while (!string.IsNullOrEmpty(tmpLine = ReadLine()))
            {
                requestLines.Add(tmpLine);
            }
            return requestLines;
        }
    }
}