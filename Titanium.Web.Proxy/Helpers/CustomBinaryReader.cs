using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Titanium.Web.Proxy.Helpers
{
    public class CustomBinaryReader : BinaryReader
    {
        internal CustomBinaryReader(Stream stream, Encoding encoding)
            : base(stream, encoding)
        {
        }

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