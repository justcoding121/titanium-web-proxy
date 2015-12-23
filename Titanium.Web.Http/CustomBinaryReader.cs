using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Titanium.Web.Http
{
  public class HttpStreamReader
    {

      public async static Task<string> ReadLine(Stream stream)
        {
            var buf = new byte[2];
            var readBuffer = new StringBuilder();
            try
            {
                while ((await stream.ReadAsync(buf, 0, 2)) > 0)
                {
                    var charRead = System.Text.Encoding.ASCII.GetString(buf);
                    if (charRead == Environment.NewLine)
                    {
                        return readBuffer.ToString();
                    }
                    readBuffer.Append(charRead);
                }
                return readBuffer.ToString();
            }
            catch (IOException)
            {
                return readBuffer.ToString();
            }
        }


       public async static Task<List<string>> ReadAllLines(Stream stream)
        {
            string tmpLine;
            var requestLines = new List<string>();
            while (!string.IsNullOrEmpty(tmpLine = await ReadLine(stream)))
            {
                requestLines.Add(tmpLine);
            }
            return requestLines;
        }
    }
}