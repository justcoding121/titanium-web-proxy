using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Network.Certificate;

namespace Titanium.Web.Proxy.Network
{
    /// <summary>
    /// Certificate Engine option
    /// </summary>
    public enum CertificateEngine
    {
        /// <summary>
        /// Uses Windows Certification Generation API
        /// </summary>
        DefaultWindows = 0,

        /// <summary>
        /// Uses BouncyCastle 3rd party library
        /// </summary>
        BouncyCastle = 1
    }

   
 

    /// <summary>
    /// A class to manage SSL certificates used by this proxy server
    /// </summary>
    public sealed class CertificateManager : IDisposable
    {
        internal CertificateEngine Engine
        {
            get => engine;
            set
            {
                //For Mono (or Non-Windows) only Bouncy Castle is supported
                if (!RunTime.IsWindows || RunTime.IsRunningOnMono)
                {
                    value = CertificateEngine.BouncyCastle;
                }

                if (value != engine)
                {
                    certEngine = null;
                    engine = value;
                }

                if (certEngine == null)
                {
                    certEngine = engine == CertificateEngine.BouncyCastle ? (ICertificateMaker)new BCCertificateMaker(exceptionFunc) : new WinCertificateMaker(exceptionFunc);
                }
            }
        }

        private bool trustRootCertificate;

        public bool TrustrootCertificate
        {
            get => trustRootCertificate;
            set
            {
                trustRootCertificate = value;
                 
            }
        }


        private bool saveCertificate;
        public bool SaveCertificate
        {
            get => saveCertificate;
            set
            {
                saveCertificate = value; 
            }
        }


        private const string defaultRootCertificateIssuer = "Titanium";

        private const string defaultRootRootCertificateName = "Titanium Root Certificate Authority";

        private CertificateEngine engine;

        private ICertificateMaker certEngine;

        private string issuer;

        private string rootCertificateName;

        private bool clearCertificates { get; set; }

        private X509Certificate2 rootCertificate;

        private bool overwriteRootCert;

        /// <summary>
        /// Cache dictionary
        /// </summary>
        private readonly IDictionary<string, CachedCertificate> certificateCache;

        private readonly Action<Exception> exceptionFunc;

         
        internal string Issuer
        {
            get => issuer ?? defaultRootCertificateIssuer;
            set
            {
                issuer = value;
                //ClearRootCertificate();
            }
        }

        internal string RootCertificateName
        {
            get => rootCertificateName ?? defaultRootRootCertificateName;
            set
            {
                rootCertificateName = value;
                //ClearRootCertificate();
            }
        }

        internal X509Certificate2 RootCertificate
        {
            get => rootCertificate;
            set
            {
                ClearRootCertificate();
                rootCertificate = value;
            }
        }


        private string password_rootCert = string.Empty;

        public string Password_rootCert
        {
            get => password_rootCert;
            set
            {
                password_rootCert = value;
            }
        }

        /// <summary>
        /// Is the root certificate used by this proxy is valid?
        /// </summary>
        internal bool CertValidated => RootCertificate != null;


        internal CertificateManager(Action<Exception> exceptionFunc)
        {
            this.exceptionFunc = exceptionFunc;
            Engine = CertificateEngine.BouncyCastle;

            certificateCache = new ConcurrentDictionary<string, CachedCertificate>();
        }

        public void ClearRootCertificate()
        {
            certificateCache.Clear();
            rootCertificate = null;
        }


        private string GetRootCertificateDirectory()
        {
            string assemblyLocation = Assembly.GetExecutingAssembly().Location;

            // dynamically loaded assemblies returns string.Empty location
            if (assemblyLocation == string.Empty)
            {
                assemblyLocation = Assembly.GetEntryAssembly().Location;
            }

            string path = Path.GetDirectoryName(assemblyLocation);
            if (null == path)
                throw new NullReferenceException(); 
            return path;
        }


        private string GetCertPath()
        {
            string path = GetRootCertificateDirectory();

            string certPath = Path.Combine(path, "crts");
            if (!Directory.Exists(certPath)) { Directory.CreateDirectory(certPath); }
            return certPath;
        }

        private string GetRootCertificatePath()
        {
            string path = GetRootCertificateDirectory();
             
            //string fileName = Path.Combine(path, "rootCert.pfx");
            string fileName = filename_loadx;
            if ((fileName == string.Empty))
            { 
                fileName = Path.Combine(path, "rootCert.pfx");
                passwordx = password_rootCert; /*string.Empty;*/
                StorageFlag = X509KeyStorageFlags.Exportable;
                this.overwriteRootCert = true;
            }
            return fileName;
        }

        public X509Certificate2 LoadRootCertificate()
        {
            string fileName = GetRootCertificatePath();
             
            if (!File.Exists(fileName))
            { 
                return null;
            }
               
            try
            { 
                //return new X509Certificate2(fileName, string.Empty, X509KeyStorageFlags.Exportable);
                return new X509Certificate2(fileName, passwordx, StorageFlag);
            }
            catch (Exception e)
            { 
                exceptionFunc(e);
                return null;
            }
        }


  



      
            /// <summary>
            /// Attempts to create a RootCertificate
            /// </summary>
            /// <param name="persistToFile">if set to <c>true</c> try to load/save the certificate from rootCert.pfx.</param>
            /// <returns>
            /// true if succeeded, else false
            /// </returns>
       public bool CreateTrustedRootCertificate(bool persistToFile = true)
        { 
            if (persistToFile && RootCertificate == null)
            {
                
                    RootCertificate = LoadRootCertificate();               
                  
            }

            if (RootCertificate != null)
            { 
                return true;
            }
            else
            {
                if (overwriteRootCert == false)
                { 
                    return false;
                }
            }

             

            try
            { 
                RootCertificate = CreateCertificate(RootCertificateName, true);                 
            }
            catch (Exception e)
            {
                exceptionFunc(e);
            }

            if (persistToFile && RootCertificate != null)
            {
                try
                {
                    try {
                        Directory.Delete(GetCertPath(), true); 
                    } catch { }
                    string fileName = GetRootCertificatePath(); 
                    File.WriteAllBytes(fileName, RootCertificate.Export(X509ContentType.Pkcs12, password_rootCert)); 
                }
                catch (Exception e)
                { 
                    exceptionFunc(e);
                }
            }

 
         

            return RootCertificate != null;
        }



        /// <summary>
        /// Manually load a RootCertificate
        /// set password and path_fileRootCert
        /// </summary> 
        /// <param name="fileName"></param>
        /// <param name="password"></param>
        /// <param name="overwriteRootCert"></param>
        /// <param name="StorageFlag"></param>
        /// <returns>
        /// true if succeeded, else false
        /// </returns>
        public void SetInfo_LoadRootCertificate(string fileName, string password, bool overwriteRootCert = true, X509KeyStorageFlags StorageFlag = X509KeyStorageFlags.Exportable)
        {
            filename_loadx = fileName;
           this.passwordx = password;
            this.password_rootCert = passwordx;
            this.StorageFlag = StorageFlag;
            this.overwriteRootCert = overwriteRootCert; 
        }

        /// <summary>
        /// Manually load a RootCertificate
        /// </summary> 
        /// <param name="fileName"></param>
        /// <param name="password"></param>
        /// <param name="overwriteRootCert"></param>
        /// <param name="StorageFlag"></param>
        /// <returns>
        /// true if succeeded, else false
        /// </returns>
        public bool LoadRootCertificate(string fileName, string password, bool overwriteRootCert = true, X509KeyStorageFlags StorageFlag = X509KeyStorageFlags.Exportable)
        {
            SetInfo_LoadRootCertificate(fileName, password, overwriteRootCert, StorageFlag);
             RootCertificate = LoadRootCertificate(); 

            return (RootCertificate != null);

        }
      
        private string passwordx = string.Empty;
        private string filename_loadx = "";
        X509KeyStorageFlags StorageFlag = X509KeyStorageFlags.Exportable;
         


        /// <summary>
        /// Trusts the root certificate.
        /// </summary>
        public void TrustRootCertificate()
        {
            //current user
            TrustRootCertificate(StoreLocation.CurrentUser);

            //current system
            TrustRootCertificate(StoreLocation.LocalMachine);
        }

 


        /// <summary>
        /// Puts the certificate to the local machine's certificate store. 
        /// Needs elevated permission. Works only on Windows.
        /// </summary>
        /// <returns></returns>
        public bool TrustRootCertificateAsAdministrator()
        {
            if (!RunTime.IsWindows || RunTime.IsRunningOnMono)
            {
                return false;
            }

            string fileName = Path.GetTempFileName();
            File.WriteAllBytes(fileName, RootCertificate.Export(X509ContentType.Pkcs12, password_rootCert));

            var info = new ProcessStartInfo
            {
                FileName = "certutil.exe",
                Arguments = "-importPFX -p \""+ password_rootCert + "\" -f \"" + fileName + "\"",
                CreateNoWindow = true,
                UseShellExecute = true,
                Verb = "runas",
                ErrorDialog = false,
            };

            try
            {
                var process = Process.Start(info);
                if (process == null)
                {
                    return false;
                }

                process.WaitForExit();

                File.Delete(fileName);
            }
            catch
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Removes the trusted certificates.
        /// </summary>
        public void RemoveTrustedRootCertificates()
        {
            //current user
            RemoveTrustedRootCertificates(StoreLocation.CurrentUser);

            //current system
            RemoveTrustedRootCertificates(StoreLocation.LocalMachine);
        }

        /// <summary>
        /// Removes the trusted certificates from the local machine's certificate store. 
        /// Needs elevated permission. Works only on Windows.
        /// </summary>
        /// <returns></returns>
        public bool RemoveTrustedRootCertificatesAsAdministrator()
        {
            if (!RunTime.IsWindows || RunTime.IsRunningOnMono)
            {
                return false;
            }

            var info = new ProcessStartInfo
            {
                FileName = "certutil.exe",
                Arguments = "-delstore Root \"" + RootCertificateName + "\"",
                CreateNoWindow = true,
                UseShellExecute = true,
                Verb = "runas",
                ErrorDialog = false,
            };

            try
            {
                var process = Process.Start(info);
                if (process == null)
                {
                    return false;
                }

                process.WaitForExit();
            }
            catch
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Determines whether the root certificate is trusted.
        /// </summary>
        public bool IsRootCertificateTrusted()
        {
            return FindRootCertificate(StoreLocation.CurrentUser) || IsRootCertificateMachineTrusted();
        }

        /// <summary>
        /// Determines whether the root certificate is machine trusted.
        /// </summary>
        public bool IsRootCertificateMachineTrusted()
        {
            return FindRootCertificate(StoreLocation.LocalMachine);
        }

        private bool FindRootCertificate(StoreLocation storeLocation)
        {
            string value = $"{RootCertificate.Issuer}";
            return FindCertificates(StoreName.Root, storeLocation, value).Count > 0;
        }

        private X509Certificate2Collection FindCertificates(StoreName storeName, StoreLocation storeLocation, string findValue)
        {
            var x509Store = new X509Store(storeName, storeLocation);
            try
            {
                x509Store.Open(OpenFlags.OpenExistingOnly);
                return x509Store.Certificates.Find(X509FindType.FindBySubjectDistinguishedName, findValue, false);
            }
            finally
            {
                x509Store.Close();
            }
        }


        private X509Certificate2 makeCertificate(string certificateName, bool isRootCertificate)
        {
            if (!isRootCertificate && RootCertificate == null)
            {
                CreateTrustedRootCertificate();
            }
            return certEngine.MakeCertificate(certificateName, isRootCertificate, RootCertificate);
        }


        /// <summary>
        /// Create an SSL certificate
        /// </summary>
        /// <param name="certificateName"></param>
        /// <param name="isRootCertificate"></param>
        /// <returns></returns>
        internal X509Certificate2 CreateCertificate(string certificateName, bool isRootCertificate)
        {
            if (certificateCache.ContainsKey(certificateName))
            {
                var cached = certificateCache[certificateName];
                cached.LastAccess = DateTime.Now;
                return cached.Certificate;
            }

            X509Certificate2 certificate = null;
            lock (string.Intern(certificateName))
            {
                if (certificateCache.ContainsKey(certificateName) == false)
                {
                    try
                    {

                        //if (!isRootCertificate && RootCertificate == null)
                        // {
                        //     CreateTrustedRootCertificate();
                        // }
                    
                        if ((isRootCertificate == false)&& (saveCertificate == true)) {
                             
                            string path = GetCertPath(); 
                            string subjectName = System.Text.RegularExpressions.Regex.Replace(certificateName.ToLower(), @"^" + "CN".ToLower() + @"\s*=\s*", ""); 
                            subjectName = subjectName.Replace("*", "$x$");
                            subjectName= Path.Combine(path, subjectName+ ".pfx");
                            if (!File.Exists(subjectName))
                            { 
                                certificate = this.makeCertificate(certificateName, isRootCertificate);
                                File.WriteAllBytes(subjectName, certificate.Export(X509ContentType.Pkcs12));
                            }
                            else
                            {
                                try
                                { 
                                    certificate = new X509Certificate2(subjectName, string.Empty, StorageFlag); 
                                }
                                catch/* (Exception e)*/
                                { 
                                    certificate = this.makeCertificate(certificateName, isRootCertificate);                                   
                                }
                            }
                        }
                        else {
                           
                            certificate = this.makeCertificate(certificateName, isRootCertificate);                           

                        }
                      
                        
                    }
                    catch (Exception e)
                    { 
                        exceptionFunc(e);
                    }
                    if (certificate != null && !certificateCache.ContainsKey(certificateName))
                    {
                        certificateCache.Add(certificateName, new CachedCertificate
                        {
                            Certificate = certificate
                        });
                    }
                }
                else
                {
                    if (certificateCache.ContainsKey(certificateName))
                    {
                        var cached = certificateCache[certificateName];
                        cached.LastAccess = DateTime.Now;
                        return cached.Certificate;
                    }
                }
        }

            return certificate;
        }

        /// <summary>
        /// Stops the certificate cache clear process
        /// </summary>
        internal void StopClearIdleCertificates()
        {
            clearCertificates = false;
        }

        /// <summary>
        /// A method to clear outdated certificates
        /// </summary>
        internal async void ClearIdleCertificates(int certificateCacheTimeOutMinutes)
        {
            clearCertificates = true;
            while (clearCertificates)
            {
                var cutOff = DateTime.Now.AddMinutes(-1 * certificateCacheTimeOutMinutes);

                var outdated = certificateCache.Where(x => x.Value.LastAccess < cutOff).ToList();

                foreach (var cache in outdated)
                    certificateCache.Remove(cache.Key);

                //after a minute come back to check for outdated certificates in cache
                await Task.Delay(1000 * 60);
            }
        }

        /// <summary>
        /// Make current machine trust the Root Certificate used by this proxy
        /// </summary>
        /// <param name="storeLocation"></param>
        /// <returns></returns>
        private void TrustRootCertificate(StoreLocation storeLocation)
        {
            if (RootCertificate == null)
            {
                exceptionFunc(
                    new Exception("Could not set root certificate"
                                  + " as system proxy since it is null or empty."));

                return;
            }

            var x509RootStore = new X509Store(StoreName.Root, storeLocation);
            var x509PersonalStore = new X509Store(StoreName.My, storeLocation);

            try
            {
                x509RootStore.Open(OpenFlags.ReadWrite);
                x509PersonalStore.Open(OpenFlags.ReadWrite);

                x509RootStore.Add(RootCertificate);
                x509PersonalStore.Add(RootCertificate);
            }
            catch (Exception e)
            { 
                exceptionFunc(
                    new Exception("Failed to make system trust root certificate "
                                  + $" for {storeLocation} store location. You may need admin rights.", e));
            }
            finally
            {
                x509RootStore.Close();
                x509PersonalStore.Close();
            }
        }

   
        /// <summary>
        /// Remove the Root Certificate trust
        /// </summary>
        /// <param name="storeLocation"></param>
        /// <returns></returns>
        private void RemoveTrustedRootCertificates(StoreLocation storeLocation)
        {
            if (RootCertificate == null)
            {
                exceptionFunc(
                    new Exception("Could not set root certificate"
                                  + " as system proxy since it is null or empty."));


                return;
            }

            var x509RootStore = new X509Store(StoreName.Root, storeLocation);
            var x509PersonalStore = new X509Store(StoreName.My, storeLocation);

            try
            {
                x509RootStore.Open(OpenFlags.ReadWrite);
                x509PersonalStore.Open(OpenFlags.ReadWrite);

                x509RootStore.Remove(RootCertificate);
                x509PersonalStore.Remove(RootCertificate);
            }
            catch (Exception e)
            {
                exceptionFunc(
                    new Exception("Failed to remove root certificate trust "
                                  + $" for {storeLocation} store location. You may need admin rights.", e));
            }
            finally
            {
                x509RootStore.Close();
                x509PersonalStore.Close();
            }
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
        }
    }
}
