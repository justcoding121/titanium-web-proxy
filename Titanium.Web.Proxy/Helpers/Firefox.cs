using System;
using System.IO;

namespace Titanium.Web.Proxy.Helpers
{
    /// <summary>
    /// A helper class to set proxy settings for firefox
    /// </summary>
    internal class FireFoxProxySettingsManager
    {
        /// <summary>
        /// Add Firefox settings.
        /// </summary>
        internal void UseSystemProxy()
        {
            try
            {
                var myProfileDirectory =
                    new DirectoryInfo(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) +
                                      "\\Mozilla\\Firefox\\Profiles\\").GetDirectories("*.default");
                var myFfPrefFile = myProfileDirectory[0].FullName + "\\prefs.js";
                if (!File.Exists(myFfPrefFile))
                {
                    return;
                }

                // We have a pref file so let''s make sure it has the proxy setting
                var myReader = new StreamReader(myFfPrefFile);
                var myPrefContents = myReader.ReadToEnd();
                myReader.Close();

                for (int i = 0; i <= 4; i++)
                {
                    var searchStr = $"user_pref(\"network.proxy.type\", {i});";

                    if (myPrefContents.Contains(searchStr))
                    {
                        // Add the proxy enable line and write it back to the file
                        myPrefContents = myPrefContents.Replace(searchStr,
                            "user_pref(\"network.proxy.type\", 5);");
                    }
                }

                File.Delete(myFfPrefFile);
                File.WriteAllText(myFfPrefFile, myPrefContents);
            }
            catch (Exception)
            {
                // Only exception should be a read/write error because the user opened up FireFox so they can be ignored.
            }
        }
    }
}
