using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Diagnostics;

namespace Titanium.Web.Proxy.Helpers
{
    public class CustomBinaryReader : BinaryReader
    {

        public CustomBinaryReader(Stream stream, Encoding encoding)
            : base(stream, encoding)
        {

        }

        public string ReadLine()
        {
            char[] buf = new char[1];
            StringBuilder _readBuffer = new StringBuilder();
            try
            {
                var charsRead = 0;
                char lastChar = new char();

                while ((charsRead = base.Read(buf, 0, 1)) > 0)
                {
                    if (lastChar == '\r' && buf[0] == '\n')
                    {
                        return _readBuffer.Remove(_readBuffer.Length - 1, 1).ToString();
                    }
                    else
                        if (buf[0] == '\0')
                        {
                            return _readBuffer.ToString();
                        }
                        else
                            _readBuffer.Append(buf[0]);

                    lastChar = buf[0];
                }
                return _readBuffer.ToString();
            }
            catch (IOException)
            { return _readBuffer.ToString(); }
            catch (Exception e)
            { throw e; }

        }

    }
}
