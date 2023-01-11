﻿using System;
using System.Runtime.InteropServices;

namespace Titanium.Web.Proxy.Network.WinAuth.Security;

internal class Common
{
    internal static uint NewContextAttributes = 0;
    internal static SecurityInteger NewLifeTime = new(0);

    #region Internal constants

    internal const int IscReqReplayDetect = 0x00000004;
    internal const int IscReqSequenceDetect = 0x00000008;
    internal const int IscReqConfidentiality = 0x00000010;
    internal const int IscReqConnection = 0x00000800;

    internal const int SecurityNativeDataRepresentation = 0x10;
    internal const int MaximumTokenSize = 12288;
    internal const int SecurityCredentialsOutbound = 2;
    internal const int SuccessfulResult = 0;
    internal const int IntermediateResult = 0x90312;

    #endregion

    #region internal enumerations

    internal enum SecurityBufferType
    {
        SecbufferVersion = 0,
        SecbufferEmpty = 0,
        SecbufferData = 1,
        SecbufferToken = 2
    }

    [Flags]
    internal enum NtlmFlags
    {
        // The client sets this flag to indicate that it supports Unicode strings.
        NegotiateUnicode = 0x00000001,

        // This is set to indicate that the client supports OEM strings.
        NegotiateOem = 0x00000002,

        // This requests that the server send the authentication target with the Type 2 reply.
        RequestTarget = 0x00000004,

        // Indicates that NTLM authentication is supported.
        NegotiateNtlm = 0x00000200,

        // When set, the client will send with the message the name of the domain in which the workstation has membership.
        NegotiateDomainSupplied = 0x00001000,

        // Indicates that the client is sending its workstation name with the message.  
        NegotiateWorkstationSupplied = 0x00002000,

        // Indicates that communication between the client and server after authentication should carry a "dummy" signature.
        NegotiateAlwaysSign = 0x00008000,

        // Indicates that this client supports the NTLM2 signing and sealing scheme; if negotiated, this can also affect the response calculations.
        NegotiateNtlm2Key = 0x00080000,

        // Indicates that this client supports strong (128-bit) encryption.
        Negotiate128 = 0x20000000,

        // Indicates that this client supports medium (56-bit) encryption.
        Negotiate56 = unchecked((int)0x80000000)
    }

    internal enum NtlmAuthLevel
    {
        /* Use LM and NTLM, never use NTLMv2 session security. */
        LmAndNtlm,

        /* Use NTLMv2 session security if the server supports it,
         * otherwise fall back to LM and NTLM. */
        LmAndNtlmAndTryNtlMv2Session,

        /* Use NTLMv2 session security if the server supports it,
         * otherwise fall back to NTLM.  Never use LM. */
        NtlmOnly,

        /* Use NTLMv2 only. */
        NtlMv2Only
    }

    #endregion

    #region internal structures

    [StructLayout(LayoutKind.Sequential)]
    internal struct SecurityHandle
    {
        internal IntPtr LowPart;
        internal IntPtr HighPart;

        internal SecurityHandle(int dummy)
        {
            LowPart = HighPart = IntPtr.Zero;
        }

        /// <summary>
        ///     Resets all internal pointers to default value
        /// </summary>
        internal void Reset()
        {
            LowPart = HighPart = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SecurityInteger
    {
        internal uint LowPart;
        internal int HighPart;

        internal SecurityInteger(int dummy)
        {
            LowPart = 0;
            HighPart = 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SecurityBuffer
    {
        internal int cbBuffer;
        internal int cbBufferType;
        internal IntPtr pvBuffer;

        internal SecurityBuffer(int bufferSize)
        {
            cbBuffer = bufferSize;
            cbBufferType = (int)SecurityBufferType.SecbufferToken;
            pvBuffer = Marshal.AllocHGlobal(bufferSize);
        }

        internal SecurityBuffer(byte[] secBufferBytes)
        {
            cbBuffer = secBufferBytes.Length;
            cbBufferType = (int)SecurityBufferType.SecbufferToken;
            pvBuffer = Marshal.AllocHGlobal(cbBuffer);
            Marshal.Copy(secBufferBytes, 0, pvBuffer, cbBuffer);
        }

        internal SecurityBuffer(byte[] secBufferBytes, SecurityBufferType bufferType)
        {
            cbBuffer = secBufferBytes.Length;
            cbBufferType = (int)bufferType;
            pvBuffer = Marshal.AllocHGlobal(cbBuffer);
            Marshal.Copy(secBufferBytes, 0, pvBuffer, cbBuffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SecurityBufferDescription
    {
        internal int ulVersion;
        internal int cBuffers;
        internal IntPtr pBuffers; // Point to SecBuffer

        internal SecurityBufferDescription(int bufferSize)
        {
            ulVersion = (int)SecurityBufferType.SecbufferVersion;
            cBuffers = 1;
            var thisSecBuffer = new SecurityBuffer(bufferSize);
            pBuffers = Marshal.AllocHGlobal(Marshal.SizeOf(thisSecBuffer));
            Marshal.StructureToPtr(thisSecBuffer, pBuffers, false);
        }

        internal SecurityBufferDescription(byte[] secBufferBytes)
        {
            ulVersion = (int)SecurityBufferType.SecbufferVersion;
            cBuffers = 1;
            var thisSecBuffer = new SecurityBuffer(secBufferBytes);
            pBuffers = Marshal.AllocHGlobal(Marshal.SizeOf(thisSecBuffer));
            Marshal.StructureToPtr(thisSecBuffer, pBuffers, false);
        }


        internal byte[]? GetBytes()
        {
            byte[]? buffer = null;

            if (pBuffers == IntPtr.Zero) throw new InvalidOperationException("Object has already been disposed!!!");

            if (cBuffers == 1)
            {
                var thisSecBuffer = (SecurityBuffer)Marshal.PtrToStructure(pBuffers, typeof(SecurityBuffer));

                if (thisSecBuffer.cbBuffer > 0)
                {
                    buffer = new byte[thisSecBuffer.cbBuffer];
                    Marshal.Copy(thisSecBuffer.pvBuffer, buffer, 0, thisSecBuffer.cbBuffer);
                }
            }
            else
            {
                var bytesToAllocate = 0;

                for (var index = 0; index < cBuffers; index++)
                {
                    // The bits were written out the following order:
                    // int cbBuffer;
                    // int BufferType;
                    // pvBuffer;
                    // What we need to do here calculate the total number of bytes we need to copy...
                    var currentOffset = index * Marshal.SizeOf(typeof(Buffer));
                    bytesToAllocate += Marshal.ReadInt32(pBuffers, currentOffset);
                }

                buffer = new byte[bytesToAllocate];

                for (int index = 0, bufferIndex = 0; index < cBuffers; index++)
                {
                    // The bits were written out the following order:
                    // int cbBuffer;
                    // int BufferType;
                    // pvBuffer;
                    // Now iterate over the individual buffers and put them together into a
                    // byte array...
                    var currentOffset = index * Marshal.SizeOf(typeof(Buffer));
                    var bytesToCopy = Marshal.ReadInt32(pBuffers, currentOffset);
                    var secBufferpvBuffer = Marshal.ReadIntPtr(pBuffers,
                        currentOffset + Marshal.SizeOf(typeof(int)) + Marshal.SizeOf(typeof(int)));
                    Marshal.Copy(secBufferpvBuffer, buffer, bufferIndex, bytesToCopy);
                    bufferIndex += bytesToCopy;
                }
            }

            return buffer;
        }
    }

    #endregion
}