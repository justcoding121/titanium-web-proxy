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
            var buf = new char[1];
            var readBuffer = new StringBuilder();
            try
            {
                var lastChar = new char();

                while ((Read(buf, 0, 1)) > 0)
                {
                    if (lastChar == '\r' && buf[0] == '\n')
                    {
                        return readBuffer.Remove(readBuffer.Length - 1, 1).ToString();
                    }
                    if (buf[0] == '\0')
                    {
                        return readBuffer.ToString();
                    }
                    readBuffer.Append(buf[0]);

                    lastChar = buf[0];
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