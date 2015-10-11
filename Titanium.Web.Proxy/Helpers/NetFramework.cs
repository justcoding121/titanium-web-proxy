using System;
using System.Net.Configuration;
using System.Reflection;

namespace Titanium.Web.Proxy.Helpers
{
    public class NetFrameworkHelper
    {
        //Fix bug in .Net 4.0 HttpWebRequest (don't use this for 4.5 and above)
        //http://stackoverflow.com/questions/856885/httpwebrequest-to-url-with-dot-at-the-end
        public static void UrlPeriodFix()
        {
            var getSyntax = typeof (UriParser).GetMethod("GetSyntax", BindingFlags.Static | BindingFlags.NonPublic);
            var flagsField = typeof (UriParser).GetField("m_Flags", BindingFlags.Instance | BindingFlags.NonPublic);
            if (getSyntax != null && flagsField != null)
            {
                foreach (var scheme in new[] {"http", "https"})
                {
                    var parser = (UriParser) getSyntax.Invoke(null, new object[] {scheme});
                    if (parser != null)
                    {
                        var flagsValue = (int) flagsField.GetValue(parser);

                        if ((flagsValue & 0x1000000) != 0)
                            flagsField.SetValue(parser, flagsValue & ~0x1000000);
                    }
                }
            }
        }

        // Enable/disable useUnsafeHeaderParsing.
        // See http://o2platform.wordpress.com/2010/10/20/dealing-with-the-server-committed-a-protocol-violation-sectionresponsestatusline/
        public static bool ToggleAllowUnsafeHeaderParsing(bool enable)
        {
            //Get the assembly that contains the internal class
            var assembly = Assembly.GetAssembly(typeof (SettingsSection));
            if (assembly != null)
            {
                //Use the assembly in order to get the internal type for the internal class
                var settingsSectionType = assembly.GetType("System.Net.Configuration.SettingsSectionInternal");
                if (settingsSectionType != null)
                {
                    //Use the internal static property to get an instance of the internal settings class.
                    //If the static instance isn't created already invoking the property will create it for us.
                    var anInstance = settingsSectionType.InvokeMember("Section",
                        BindingFlags.Static | BindingFlags.GetProperty | BindingFlags.NonPublic, null, null,
                        new object[] {});
                    if (anInstance != null)
                    {
                        //Locate the private bool field that tells the framework if unsafe header parsing is allowed
                        var aUseUnsafeHeaderParsing = settingsSectionType.GetField("useUnsafeHeaderParsing",
                            BindingFlags.NonPublic | BindingFlags.Instance);
                        if (aUseUnsafeHeaderParsing != null)
                        {
                            aUseUnsafeHeaderParsing.SetValue(anInstance, enable);
                            return true;
                        }
                    }
                }
            }
            return false;
        }
    }
}