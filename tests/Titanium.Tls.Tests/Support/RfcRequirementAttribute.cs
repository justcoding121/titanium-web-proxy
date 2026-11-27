using System;

namespace Titanium.Tls.Tests.Support;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
internal sealed class RfcRequirementAttribute : Attribute
{
    internal RfcRequirementAttribute(string rfc, string section, string id)
    {
        Rfc = rfc;
        Section = section;
        Id = id;
    }

    internal string Rfc { get; }

    internal string Section { get; }

    internal string Id { get; }
}
