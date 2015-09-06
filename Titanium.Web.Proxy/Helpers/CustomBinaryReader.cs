using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Diagnostics;

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
            char[] buf = new char[1];
            StringBuilder readBuffer = new StringBuilder();
            try
            {
                var charsRead = 0;
                char lastChar = new char();

                while ((charsRead = base.Read(buf, 0, 1)) > 0)
                {
                    if (lastChar == '\r' && buf[0] == '\n')
                    {
                        return readBuffer.Remove(readBuffer.Length - 1, 1).ToString();
                    }
                    else
                        if (buf[0] == '\0')
                        {
                            return readBuffer.ToString();
                        }
                        else
                            readBuffer.Append(buf[0]);

                    lastChar = buf[0];
                }
                return readBuffer.ToString();
            }
            catch (IOException)
            { 
                return readBuffer.ToString();
            }
            catch (Exception)
            { 
                throw;
            }

        }


        internal List<string> ReadAllLines()
        {
            string tmpLine = null;
            List<string> requestLines = new List<string>();
            while (!String.IsNullOrEmpty(tmpLine = ReadLine()))
            {
                requestLines.Add(tmpLine);
            }
            return requestLines;
        }
    }
}
