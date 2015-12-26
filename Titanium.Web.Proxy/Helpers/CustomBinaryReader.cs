using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Titanium.Web.Proxy.Helpers
{
    internal class CustomBinaryReader : BinaryReader
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

                while (true)
                {
                    var buf = ReadChar();
                    if (lastChar == '\r' && buf == '\n')
                    {
                        return readBuffer.Remove(readBuffer.Length - 1, 1).ToString();
                    }
                    if (buf == '\0')
                    {
                        return readBuffer.ToString();
                    }
                    readBuffer.Append(buf);

                    lastChar = buf;
                }

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