using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// General Information about an assembly is controlled through the following 
// set of attributes. Change these attribute values to modify the information
// associated with an assembly.

[assembly: AssemblyTitle("Titanium.Web.Proxy")]
[assembly: AssemblyDescription("")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyProduct("Titanium.Web.Proxy")]
[assembly: AssemblyCopyright("Copyright © Titanium 2015-2020")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]
[assembly: InternalsVisibleTo("Titanium.Web.Proxy.UnitTests, PublicKey=" +
                              "0024000004800000940000000602000000240000525341310004000001000100e7368e0ccc717e" +
                              "eb4d57d35ad6a8305cbbed14faa222e13869405e92c83856266d400887d857005f1393ffca2b92" +
                              "de7f3ba0bdad35ec2d6057ee1846091b34be2abc3f97dc7e72c16fd4958c15126b12923df76964" +
                              "7d84922c3f4f3b80ee0ae8e4cb40bc1973b782afb90bb00519fd16adf960f217e23696e7c31654" +
                              "01d0acd6")]
[assembly: InternalsVisibleTo("Titanium.Web.Proxy.IntegrationTests, PublicKey=" +
                              "0024000004800000940000000602000000240000525341310004000001000100e7368e0ccc717e" +
                              "eb4d57d35ad6a8305cbbed14faa222e13869405e92c83856266d400887d857005f1393ffca2b92" +
                              "de7f3ba0bdad35ec2d6057ee1846091b34be2abc3f97dc7e72c16fd4958c15126b12923df76964" +
                              "7d84922c3f4f3b80ee0ae8e4cb40bc1973b782afb90bb00519fd16adf960f217e23696e7c31654" +
                              "01d0acd6")]
// Benchmarks needs internal access (HeaderParser, HttpStream.ReadLineInternalAsync) so the
// measurement harness exercises the real parser code paths rather than a reimplementation of them.
[assembly: InternalsVisibleTo("Titanium.Web.Proxy.Benchmarks, PublicKey=" +
                              "0024000004800000940000000602000000240000525341310004000001000100e7368e0ccc717e" +
                              "eb4d57d35ad6a8305cbbed14faa222e13869405e92c83856266d400887d857005f1393ffca2b92" +
                              "de7f3ba0bdad35ec2d6057ee1846091b34be2abc3f97dc7e72c16fd4958c15126b12923df76964" +
                              "7d84922c3f4f3b80ee0ae8e4cb40bc1973b782afb90bb00519fd16adf960f217e23696e7c31654" +
                              "01d0acd6")]

// Setting ComVisible to false makes the types in this assembly not visible 
// to COM components.  If you need to access a type in this assembly from 
// COM, set the ComVisible attribute to true on that type.

[assembly: ComVisible(false)]

// The following GUID is for the ID of the typelib if this project is exposed to COM

[assembly: Guid("5036e0b7-a0d0-4070-8eb0-72c129dee9b3")]

// Version information for an assembly consists of the following four values:
//
//      Major Version
//      Minor Version 
//      Build Number
//      Revision
//
// GenerateAssemblyInfo is false for this project (see Titanium.Web.Proxy.csproj), so the SDK does
// not derive these from <VersionPrefix> the way it would for a normal project - they must be kept
// in sync with <VersionPrefix> in the csproj by hand on every version bump. A prior release let
// these drift to "1.0.1" while the NuGet package version moved on to 5.0.0, so the shipped DLL's
// file-properties version disagreed with the package it was published in. Keep both of the values
// below equal to <VersionPrefix> (as Major.Minor.Build.0) whenever that property changes.

[assembly: AssemblyVersion("5.4.0.0")]
[assembly: AssemblyFileVersion("5.4.0.0")]