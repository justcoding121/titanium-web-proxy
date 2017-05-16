﻿using System;
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
        internal void AddFirefox()
        {
            try
            {
                var myProfileDirectory =
                    new DirectoryInfo(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) +
                                      "\\Mozilla\\Firefox\\Profiles\\").GetDirectories("*.default");
                var myFfPrefFile = myProfileDirectory[0].FullName + "\\prefs.js";
                if (!File.Exists(myFfPrefFile)) return;
                // We have a pref file so let''s make sure it has the proxy setting
                var myReader = new StreamReader(myFfPrefFile);
                var myPrefContents = myReader.ReadToEnd();
                myReader.Close();
                if (!myPrefContents.Contains("user_pref(\"network.proxy.type\", 0);")) return;
                // Add the proxy enable line and write it back to the file
                myPrefContents = myPrefContents.Replace("user_pref(\"network.proxy.type\", 0);", "");

                File.Delete(myFfPrefFile);
                File.WriteAllText(myFfPrefFile, myPrefContents);
            }
            catch (Exception)
            {
                // Only exception should be a read/write error because the user opened up FireFox so they can be ignored.
            }
        }

        /// <summary>
        /// Remove firefox settings.
        /// </summary>
        internal void RemoveFirefox()
        {
            try
            {
                var myProfileDirectory =
                    new DirectoryInfo(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) +
                                      "\\Mozilla\\Firefox\\Profiles\\").GetDirectories("*.default");
                var myFfPrefFile = myProfileDirectory[0].FullName + "\\prefs.js";
                if (!File.Exists(myFfPrefFile)) return;
                // We have a pref file so let''s make sure it has the proxy setting
                var myReader = new StreamReader(myFfPrefFile);
                var myPrefContents = myReader.ReadToEnd();
                myReader.Close();
                if (!myPrefContents.Contains("user_pref(\"network.proxy.type\", 0);"))
                {
                    // Add the proxy enable line and write it back to the file
                    myPrefContents = myPrefContents + "\n\r" + "user_pref(\"network.proxy.type\", 0);";

                    File.Delete(myFfPrefFile);
                    File.WriteAllText(myFfPrefFile, myPrefContents);
                }
            }
            catch (Exception)
            {
                // Only exception should be a read/write error because the user opened up FireFox so they can be ignored.
            }
        }
    }
}
