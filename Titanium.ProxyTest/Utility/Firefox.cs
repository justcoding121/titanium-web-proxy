using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;

namespace Titanium.HTTPProxyServer.Test
{
    public class FireFoxUtility
    {
        public static void AddFirefox()
        {
            try
            {
                DirectoryInfo[] myProfileDirectory = new DirectoryInfo(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + "\\Mozilla\\Firefox\\Profiles\\").GetDirectories("*.default");
                string myFFPrefFile = myProfileDirectory[0].FullName + "\\prefs.js";
                if (File.Exists(myFFPrefFile))
                {
                    // We have a pref file so let''s make sure it has the proxy setting
                    StreamReader myReader = new StreamReader(myFFPrefFile);
                    string myPrefContents = myReader.ReadToEnd();
                    myReader.Close();
                    if (myPrefContents.Contains("user_pref(\"network.proxy.type\", 0);"))
                    {
                        // Add the proxy enable line and write it back to the file
                        myPrefContents = myPrefContents.Replace("user_pref(\"network.proxy.type\", 0);", "");

                        File.Delete(myFFPrefFile);
                        File.WriteAllText(myFFPrefFile, myPrefContents);
                    }
                }
            }
            catch (Exception)
            {
                // Only exception should be a read/write error because the user opened up FireFox so they can be ignored.
            }
        }
        public static void RemoveFirefox()
        {
            try
            {
                DirectoryInfo[] myProfileDirectory = new DirectoryInfo(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + "\\Mozilla\\Firefox\\Profiles\\").GetDirectories("*.default");
                string myFFPrefFile = myProfileDirectory[0].FullName + "\\prefs.js";
                if (File.Exists(myFFPrefFile))
                {
                    // We have a pref file so let''s make sure it has the proxy setting
                    StreamReader myReader = new StreamReader(myFFPrefFile);
                    string myPrefContents = myReader.ReadToEnd();
                    myReader.Close();
                    if (!myPrefContents.Contains("user_pref(\"network.proxy.type\", 0);"))
                    {
                        // Add the proxy enable line and write it back to the file
                        myPrefContents = myPrefContents + "\n\r" + "user_pref(\"network.proxy.type\", 0);";

                        File.Delete(myFFPrefFile);
                        File.WriteAllText(myFFPrefFile, myPrefContents);
                    }
                }
            }
            catch (Exception)
            {
                // Only exception should be a read/write error because the user opened up FireFox so they can be ignored.
            }
        }
    }
}
