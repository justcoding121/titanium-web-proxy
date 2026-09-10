namespace Titanium.Inspector.Services;

/// <summary>Why an HTTPS session stayed opaque (CONNECT tunnel or undecrypted).</summary>
public enum OpaqueTunnelReason
{
  None = 0,
  DecryptOff,
  BuiltInIdentity,
  BuiltInPinning,
  UserSkipList,
  UserOnlyList,
}
