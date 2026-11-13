using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Vortice.MediaFoundation;

namespace LanScreenShare.Media
{
    public enum EncoderGpuVendor
    {
        Software,
        Nvidia,
        Amd,
        Intel,
        Qualcomm,
        OtherHardware
    }

    public record MftEncoderInfo(
        string FriendlyName,
        string DisplayName,
        bool IsHardware,
        bool IsAsync,
        EncoderGpuVendor Vendor,
        Guid Clsid,
        int Priority
    );

    [SupportedOSPlatform("windows")]
    public static class HardwareMftEncoderDiscovery
    {
        public static readonly Guid MFT_CATEGORY_VIDEO_ENCODER = new("f79eac7d-e545-4387-bdee-d647d7bde42a");
        public static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
        public static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");
        public static readonly Guid MFT_FRIENDLY_NAME_Attribute = new("314ffbae-5b41-4c95-9c19-4e7d586face3");
        public static readonly Guid MFT_TRANSFORM_CLSID_Attribute = new("6821c42b-65a4-4e82-99bc-9a88205ecdc0");
        public static readonly Guid MF_TRANSFORM_ASYNC = new("8920E046-2917-4F15-B624-9121E93F62D5");
        public static readonly Guid MF_TRANSFORM_ASYNC_UNLOCK = new("e5666d6b-3422-4eb6-a421-da7db1f8e207");
        public static readonly Guid CLSID_CMSH264EncoderMFT = new("6ca510d0-568d-4e22-8a2d-97254c7c59d8");
        public static readonly Guid IID_IMFTransform = new("bf94c121-5b05-4e6f-8000-ba598961414d");

        private const uint MFT_ENUM_FLAG_SYNCMFT = 0x00000001;
        private const uint MFT_ENUM_FLAG_ASYNCMFT = 0x00000002;
        private const uint MFT_ENUM_FLAG_HARDWARE = 0x00000004;
        private const uint MFT_ENUM_FLAG_SORTANDFILTER = 0x00000040;

        [StructLayout(LayoutKind.Sequential)]
        private struct MFT_REGISTER_TYPE_INFO
        {
            public Guid guidMajorType;
            public Guid guidSubtype;
        }

        [ComImport]
        [Guid("7FEE9E9A-4A89-47A6-899C-B6A53A70FB67")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFActivate
        {
            [PreserveSig] int GetItem([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, IntPtr pValue);
            [PreserveSig] int GetItemType([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, out int pType);
            [PreserveSig] int CompareItem([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, IntPtr Value, out bool pbResult);
            [PreserveSig] int Compare(IntPtr pTheirs, int MatchType, out bool pbResult);
            [PreserveSig] int GetUINT32([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, out uint punValue);
            [PreserveSig] int GetUINT64([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, out ulong punValue);
            [PreserveSig] int GetDouble([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, out double pfValue);
            [PreserveSig] int GetGUID([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, out Guid pguidValue);
            [PreserveSig] int GetStringLength([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, out uint pcchLength);
            [PreserveSig] int GetString([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pwszValue, uint cchBufSize, out uint pcchLength);
            [PreserveSig] int GetAllocatedString([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] out string ppwszValue, out uint pcchLength);
            [PreserveSig] int GetBlobSize([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, out uint pcbBlobSize);
            [PreserveSig] int GetBlob([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [Out, MarshalAs(UnmanagedType.LPArray)] byte[] pBuf, uint cbBufSize, out uint pcbBlobSize);
            [PreserveSig] int GetAllocatedBlob([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
            [PreserveSig] int GetUnknown([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
            [PreserveSig] int SetItem([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, IntPtr Value);
            [PreserveSig] int DeleteItem([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey);
            [PreserveSig] int DeleteAllItems();
            [PreserveSig] int SetUINT32([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, uint unValue);
            [PreserveSig] int SetUINT64([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, ulong unValue);
            [PreserveSig] int SetDouble([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, double fValue);
            [PreserveSig] int SetGUID([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In, MarshalAs(UnmanagedType.LPStruct)] Guid guidValue);
            [PreserveSig] int SetString([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In, MarshalAs(UnmanagedType.LPWStr)] string wszValue);
            [PreserveSig] int SetBlob([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In, MarshalAs(UnmanagedType.LPArray)] byte[] pBuf, uint cbBufSize);
            [PreserveSig] int SetUnknown([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidKey, [In, MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
            [PreserveSig] int LockStore();
            [PreserveSig] int UnlockStore();
            [PreserveSig] int GetCount(out uint pcItems);
            [PreserveSig] int GetItemByIndex(uint unIndex, out Guid pGuidKey, IntPtr pValue);
            [PreserveSig] int CopyAllItems(IntPtr pDest);

            [PreserveSig] int ActivateObject([In, MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
            [PreserveSig] int ShutdownObject();
            [PreserveSig] int DetachObject();
        }

        [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int MFTEnumEx(
            Guid guidCategory,
            uint flags,
            IntPtr pInputType,
            IntPtr pOutputType,
            out IntPtr pppMFTActivate,
            out uint pnumMFTActivate);

        [DllImport("ole32.dll")]
        private static extern void CoTaskMemFree(IntPtr pv);

        private static bool HasActivates(int hr, uint count, IntPtr ptr) =>
            hr == 0 && count > 0 && ptr != IntPtr.Zero;

        private static IntPtr AllocateH264OutputType()
        {
            var outputType = new MFT_REGISTER_TYPE_INFO
            {
                guidMajorType = MFMediaType_Video,
                guidSubtype = MFVideoFormat_H264
            };

            IntPtr pOutputType = Marshal.AllocHGlobal(Marshal.SizeOf<MFT_REGISTER_TYPE_INFO>());
            Marshal.StructureToPtr(outputType, pOutputType, false);
            return pOutputType;
        }

        private static MftEncoderInfo? ProcessSingleEncoderActivate(IntPtr pActivate)
        {
            try
            {
                var activate = (IMFActivate)Marshal.GetObjectForIUnknown(pActivate);
                activate.GetAllocatedString(MFT_FRIENDLY_NAME_Attribute, out string friendlyName, out _);
                friendlyName ??= "Desconocido";

                activate.GetGUID(MFT_TRANSFORM_CLSID_Attribute, out Guid clsid);
                activate.GetUINT32(MF_TRANSFORM_ASYNC, out uint isAsyncVal);
                bool isAsync = isAsyncVal != 0;

                var (vendor, isHardware, displayName, priority) = CategorizeEncoder(friendlyName, isAsync);

                return new MftEncoderInfo(
                    FriendlyName: friendlyName,
                    DisplayName: displayName,
                    IsHardware: isHardware,
                    IsAsync: isAsync,
                    Vendor: vendor,
                    Clsid: clsid,
                    Priority: priority
                );
            }
            catch
            {
                return null;
            }
            finally
            {
                Marshal.Release(pActivate);
            }
        }

        public static IReadOnlyList<MftEncoderInfo> EnumerateEncoders()
        {
            var encoders = new List<MftEncoderInfo>();
            if (!OperatingSystem.IsWindows())
                return encoders;

            IntPtr pOutputType = AllocateH264OutputType();

            try
            {
                uint flags = MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_ASYNCMFT | MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER;
                int hr = MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER, flags, IntPtr.Zero, pOutputType, out IntPtr ppActivates, out uint numActivates);

                if (HasActivates(hr, numActivates, ppActivates))
                {
                    for (int i = 0; i < numActivates; i++)
                    {
                        IntPtr pActivate = Marshal.ReadIntPtr(ppActivates, i * IntPtr.Size);
                        if (pActivate == IntPtr.Zero)
                            continue;

                        var info = ProcessSingleEncoderActivate(pActivate);
                        if (info != null)
                        {
                            encoders.Add(info);
                        }
                    }

                    CoTaskMemFree(ppActivates);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HardwareMftDiscovery] Error enumerando MFTs: {ex.Message}");
            }
            finally
            {
                Marshal.FreeHGlobal(pOutputType);
            }

            encoders.Sort((a, b) => b.Priority.CompareTo(a.Priority));
            return encoders;
        }

        private static bool MatchesAny(string text, params string[] patterns)
        {
            foreach (var p in patterns)
            {
                if (text.Contains(p)) return true;
            }
            return false;
        }

        private static (EncoderGpuVendor Vendor, bool IsHardware, string DisplayName, int Priority) CategorizeEncoder(string friendlyName, bool isAsync)
        {
            string lower = friendlyName.ToLowerInvariant();

            if (MatchesAny(lower, "nvidia", "nvenc"))
            {
                return (EncoderGpuVendor.Nvidia, true, "GPU NVIDIA (NVENC)", 100);
            }
            if (MatchesAny(lower, "amd", "radeon"))
            {
                return (EncoderGpuVendor.Amd, true, "GPU AMD (Radeon)", 95);
            }
            if (MatchesAny(lower, "intel", "quicksync", "qsv"))
            {
                return (EncoderGpuVendor.Intel, true, "GPU Intel (QuickSync)", 90);
            }
            if (MatchesAny(lower, "qualcomm", "snapdragon"))
            {
                return (EncoderGpuVendor.Qualcomm, true, "GPU Qualcomm", 85);
            }
            if (MatchesAny(lower, "dx12", "hardware"))
            {
                return (EncoderGpuVendor.OtherHardware, true, $"GPU ({friendlyName})", 70);
            }

            // Software encoder (H264 Encoder MFT)
            return (EncoderGpuVendor.Software, false, "CPU (Software Microsoft)", 10);
        }

        private static IMFTransform? TryActivateMatchingObject(IntPtr pActivate, string targetFriendlyName)
        {
            try
            {
                var activate = (IMFActivate)Marshal.GetObjectForIUnknown(pActivate);
                activate.GetAllocatedString(MFT_FRIENDLY_NAME_Attribute, out string friendlyName, out _);
                friendlyName ??= "";

                if (!friendlyName.Equals(targetFriendlyName, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                int hrAct = activate.ActivateObject(IID_IMFTransform, out IntPtr pTransform);
                if (hrAct == 0 && pTransform != IntPtr.Zero)
                {
                    return new IMFTransform(pTransform);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HardwareMftDiscovery] Error activando {targetFriendlyName}: {ex.Message}");
            }
            finally
            {
                Marshal.Release(pActivate);
            }
            return null;
        }

        public static (IMFTransform? Transform, bool IsAsync) ActivateEncoder(MftEncoderInfo encoder)
        {
            if (!OperatingSystem.IsWindows())
                return (null, false);

            IntPtr pOutputType = AllocateH264OutputType();
            IMFTransform? resultTransform = null;
            bool resultIsAsync = encoder.IsAsync;

            try
            {
                uint flags = MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_ASYNCMFT | MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER;
                int hr = MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER, flags, IntPtr.Zero, pOutputType, out IntPtr ppActivates, out uint numActivates);

                if (HasActivates(hr, numActivates, ppActivates))
                {
                    try
                    {
                        for (int i = 0; i < numActivates; i++)
                        {
                            IntPtr pActivate = Marshal.ReadIntPtr(ppActivates, i * IntPtr.Size);
                            if (pActivate == IntPtr.Zero)
                                continue;

                            if (resultTransform == null)
                            {
                                resultTransform = TryActivateMatchingObject(pActivate, encoder.FriendlyName);
                            }
                            else
                            {
                                Marshal.Release(pActivate);
                            }
                        }
                    }
                    finally
                    {
                        CoTaskMemFree(ppActivates);
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pOutputType);
            }

            return (resultTransform, resultIsAsync);
        }
    }
}
