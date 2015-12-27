using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;


namespace Titanium.Web.Proxy.Http
{

    //public class HttpStreamReader
    //{
    //    public static async Task<string> ReadLine(Stream stream)
    //    {
    //        var readBuffer = new StringBuilder();

    //        try
    //        {
    //            var lastChar = default(char);
    //            var buffer = new byte[1];

    //            while ((await stream.ReadAsync(buffer, 0, 1)) > 0)
    //            {
    //                if (lastChar == '\r' && buffer[0] == '\n')
    //                {
    //                    return readBuffer.Remove(readBuffer.Length - 1, 1).ToString();
    //                }
    //                if (buffer[0] == '\0')
    //                {
    //                    return readBuffer.ToString();
    //                }
    //                readBuffer.Append(Encoding.ASCII.GetString(buffer));
    //                lastChar = Encoding.ASCII.GetChars(buffer)[0];
    //            }

    //            return readBuffer.ToString();
    //        }
    //        catch (IOException)
    //        {
    //            return readBuffer.ToString();
    //        }

    //    }

    //    public static async Task<List<string>> ReadAllLines(Stream stream)
    //    {
    //        string tmpLine;
    //        var requestLines = new List<string>();
    //        while (!string.IsNullOrEmpty(tmpLine = await ReadLine(stream)))
    //        {
    //            requestLines.Add(tmpLine);
    //        }
    //        return requestLines;
    //    }
    //}

}
