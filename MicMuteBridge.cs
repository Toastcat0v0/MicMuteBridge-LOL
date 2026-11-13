// MicMuteBridge v1.2.17 Beta - recorded desktop keyboard and mouse shortcuts
// Uses Windows Core Audio, Windows key remapping and optional desktop hotkeys.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.IO;
using System.Linq;
using System.Text;
using System.Media;
using System.Threading;
using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;

namespace MicMuteBridge
{
    internal enum EDataFlow { Render = 0, Capture = 1, All = 2 }
    internal enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumeratorComObject { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    internal interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);
        void GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);
        void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IMMDevice device);
        void RegisterEndpointNotificationCallback(IntPtr callback);
        void UnregisterEndpointNotificationCallback(IntPtr callback);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    internal interface IMMDeviceCollection
    {
        void GetCount(out uint count);
        void Item(uint index, out IMMDevice device);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    internal interface IMMDevice
    {
        void Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object result);
        void OpenPropertyStore(uint access, out IPropertyStore store);
        void GetId(out IntPtr deviceId);
        void GetState(out uint state);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    internal interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey
    {
        public Guid formatId;
        public uint propertyId;
    }

    [StructLayout(LayoutKind.Explicit, Size=24)]
    internal struct PropVariant
    {
        [FieldOffset(0)] public ushort type;
        [FieldOffset(8)] public IntPtr pointer;
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    internal interface IAudioEndpointVolume
    {
        void RegisterControlChangeNotify(IntPtr callback);
        void UnregisterControlChangeNotify(IntPtr callback);
        void GetChannelCount(out uint count);
        void SetMasterVolumeLevel(float level, IntPtr eventContext);
        void SetMasterVolumeLevelScalar(float level, IntPtr eventContext);
        void GetMasterVolumeLevel(out float level);
        void GetMasterVolumeLevelScalar(out float level);
        void SetChannelVolumeLevel(uint channel, float level, IntPtr eventContext);
        void SetChannelVolumeLevelScalar(uint channel, float level, IntPtr eventContext);
        void GetChannelVolumeLevel(uint channel, out float level);
        void GetChannelVolumeLevelScalar(uint channel, out float level);
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, IntPtr eventContext);
        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        void GetVolumeStepInfo(out uint step, out uint stepCount);
        void VolumeStepUp(IntPtr eventContext);
        void VolumeStepDown(IntPtr eventContext);
        void QueryHardwareSupport(out uint mask);
        void GetVolumeRange(out float min, out float max, out float increment);
    }

    internal class AudioDevice
    {
        public string Id;
        public string Name;
        public override string ToString() { return Name; }
    }

    internal sealed class MicStateEntry
    {
        public string Id;
        public string Name;
        public bool? Muted;
        public string Problem;
    }

    internal enum AllMicAction { Hold, Mute, Unmute }

    internal static class AllMicToggleDecision
    {
        public static AllMicAction Decide(int readable, int muted, int failures,
            IEnumerable<string> readableIds, HashSet<string> unmuteRetryIds, bool explicitUnmute = false)
        {
            if (readable == 0) return AllMicAction.Hold;
            // Retry a partial unmute only for the same devices. Wait for a
            // complete scan before deciding whether the device set has changed.
            bool retry = unmuteRetryIds.Count > 0 &&
                (failures > 0 || (muted > 0 && unmuteRetryIds.SetEquals(readableIds)));
            if (!explicitUnmute && !retry && muted < readable) return AllMicAction.Mute;
            return failures == 0 ? AllMicAction.Unmute : AllMicAction.Hold;
        }
    }

    internal class AudioSystem
    {
        private readonly IMMDeviceEnumerator enumerator;
        private readonly PropertyKey friendlyKey = new PropertyKey {
            formatId = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), propertyId = 14
        };
        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant value);

        public AudioSystem()
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        }

        public string DefaultId(EDataFlow flow, ERole role)
        {
            IMMDevice dev = null;
            try
            {
                enumerator.GetDefaultAudioEndpoint(flow, role, out dev);
                return IdOf(dev);
            }
            catch { return null; }
            finally { if (dev != null) Marshal.ReleaseComObject(dev); }
        }

        private static string IdOf(IMMDevice dev)
        {
            IntPtr pointer;
            dev.GetId(out pointer);
            try { return Marshal.PtrToStringUni(pointer); }
            finally { Marshal.FreeCoTaskMem(pointer); }
        }

        public List<AudioDevice> List(EDataFlow flow)
        {
            int skipped;
            return List(flow, out skipped);
        }

        public List<AudioDevice> List(EDataFlow flow, out int skipped)
        {
            skipped = 0;
            var results = new List<AudioDevice>();
            IMMDeviceCollection collection = null;
            try
            {
                enumerator.EnumAudioEndpoints(flow, 0x1, out collection); // DEVICE_STATE_ACTIVE
                uint count;
                collection.GetCount(out count);
                for (uint i = 0; i < count; i++)
                {
                    IMMDevice dev = null;
                    IPropertyStore store = null;
                    try
                    {
                        collection.Item(i, out dev);
                        var id = IdOf(dev);
                        if (String.IsNullOrEmpty(id)) throw new InvalidDataException("音频设备缺少 ID");
                        var name = id;
                        // A broken device property should not hide other working microphones.
                        try
                        {
                            dev.OpenPropertyStore(0, out store); // STGM_READ
                            PropertyKey key = friendlyKey;
                            PropVariant variant = new PropVariant();
                            try
                            {
                                store.GetValue(ref key, out variant);
                                if (variant.type == 31 && variant.pointer != IntPtr.Zero) // VT_LPWSTR
                                    name = Marshal.PtrToStringUni(variant.pointer);
                            }
                            finally { PropVariantClear(ref variant); }
                        }
                        catch (Exception ex)
                        {
                            Diagnostics.WriteError("设备名称读取失败，使用 ID：" + ex.Message);
                        }
                        results.Add(new AudioDevice { Id=id, Name=name });
                    }
                    catch (Exception ex)
                    {
                        // Do not abort enumeration when one endpoint is inaccessible.
                        skipped++;
                        Diagnostics.WriteError("枚举音频设备 " + flow + " 第 " + (i + 1) + " 项失败：" + ex.Message);
                    }
                    finally
                    {
                        if (store != null) Marshal.ReleaseComObject(store);
                        if (dev != null) Marshal.ReleaseComObject(dev);
                    }
                }
            }
            finally { if (collection != null) Marshal.ReleaseComObject(collection); }
            return results;
        }

        public IAudioEndpointVolume Volume(string id)
        {
            IMMDevice dev = null;
            try
            {
                enumerator.GetDevice(id, out dev);
                Guid iid = typeof(IAudioEndpointVolume).GUID;
                object obj;
                dev.Activate(ref iid, 23, IntPtr.Zero, out obj); // CLSCTX_ALL
                return (IAudioEndpointVolume)obj;
            }
            finally { if (dev != null) Marshal.ReleaseComObject(dev); }
        }
    }

    internal sealed class LocalStorage
    {
        public static readonly LocalStorage Current = new LocalStorage(AppDomain.CurrentDomain.BaseDirectory);
        public readonly string SettingsFolder;
        public readonly string LogsFolder;
        public readonly string ErrorsFolder;

        public LocalStorage(string programFolder)
        {
            SettingsFolder = Path.Combine(programFolder, "Settings");
            LogsFolder = Path.Combine(programFolder, "Logs");
            ErrorsFolder = Path.Combine(programFolder, "Errors");
        }

        public void Prepare(string legacyFolder, Action<string> log, Action<string> logError, bool promptUser = false)
        {
            Directory.CreateDirectory(LogsFolder);
            Directory.CreateDirectory(ErrorsFolder);
            // 1. 直接覆盖 EXE 更新：当前目录已有 Settings 则直接沿用
            if (Directory.Exists(SettingsFolder)) return;

            string sourceSettings = (!String.IsNullOrEmpty(legacyFolder) && Directory.Exists(legacyFolder)) ? legacyFolder : null;
            string sourceLogs = sourceSettings != null ? Path.Combine(sourceSettings, "logs") : null;

            // 2. 同级目录自动继承：扫描上一级目录下的兄弟文件夹，寻找最新的 Settings\settings.txt
            if (sourceSettings == null)
            {
                string sibling = FindSiblingSettings();
                if (sibling != null)
                {
                    sourceSettings = sibling;
                    DirectoryInfo parent = Directory.GetParent(sibling);
                    if (parent != null) sourceLogs = Path.Combine(parent.FullName, "Logs");
                }
            }

            // 3. 跨目录手动选择导入：仅在用户正常启动且未找到任何旧设置时询问
            if (sourceSettings == null && promptUser)
            {
                string picked = PromptForOldFolder();
                if (picked != null)
                {
                    sourceSettings = picked;
                    DirectoryInfo parent = Directory.GetParent(picked);
                    if (parent != null) sourceLogs = Path.Combine(parent.FullName, "Logs");
                }
            }

            if (sourceSettings != null && Directory.Exists(sourceSettings))
            {
                if (CopyLegacyFolder(sourceSettings, SettingsFolder, "settings.txt", "settings.txt.bak", "mute-recovery.txt",
                    Path.Combine("Sounds", "custom_mute.wav"), Path.Combine("Sounds", "custom_unmute.wav")))
                    log("已自动迁移旧版设置资料到当前目录的 Settings（来源：" + sourceSettings + "）。");
                if (!String.IsNullOrEmpty(sourceLogs) && Directory.Exists(sourceLogs))
                {
                    try
                    {
                        if (CopyLegacyFolder(sourceLogs, Path.Combine(LogsFolder, "Legacy"),
                            "MicMuteBridge.log", "MicMuteBridge.previous.log"))
                            log("已复制旧版运行日志到 Logs\\Legacy。");
                    }
                    catch (Exception ex) { logError("复制旧版运行日志失败，可手动复制：" + ex.ToString()); }
                }
            }
            Directory.CreateDirectory(SettingsFolder);
        }

        private string FindSiblingSettings()
        {
            try
            {
                string baseDir = Path.GetDirectoryName(SettingsFolder);
                if (String.IsNullOrEmpty(baseDir)) return null;
                DirectoryInfo currentDir = new DirectoryInfo(baseDir);
                DirectoryInfo parentDir = currentDir.Parent;
                if (parentDir == null || !parentDir.Exists) return null;

                string bestFolder = null;
                DateTime latestTime = DateTime.MinValue;
                foreach (DirectoryInfo sibling in parentDir.GetDirectories())
                {
                    if (String.Equals(sibling.FullName, currentDir.FullName, StringComparison.OrdinalIgnoreCase)) continue;
                    string candidate = Path.Combine(sibling.FullName, "Settings");
                    string file = Path.Combine(candidate, "settings.txt");
                    if (File.Exists(file))
                    {
                        DateTime writeTime = File.GetLastWriteTimeUtc(file);
                        if (writeTime > latestTime)
                        {
                            latestTime = writeTime;
                            bestFolder = candidate;
                        }
                    }
                }
                return bestFolder;
            }
            catch { return null; }
        }

        private static string PromptForOldFolder()
        {
            try
            {
                DialogResult answer = MessageBox.Show(
                    "检测到当前目录尚未配置设置。\n\n如果你电脑上其它位置有旧版 MicMuteBridge 文件夹，是否现在选择该文件夹并导入原有的设置与自定义音效？\n（首次使用请点「否」）",
                    "MicMuteBridge · 导入旧版设置",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);
                if (answer != DialogResult.Yes) return null;

                using (var dialog = new FolderBrowserDialog())
                {
                    dialog.Description = "请选择旧版 MicMuteBridge 所在的文件夹（或其内部的 Settings 文件夹）：";
                    dialog.ShowNewFolderButton = false;
                    if (dialog.ShowDialog() != DialogResult.OK || String.IsNullOrWhiteSpace(dialog.SelectedPath))
                        return null;

                    string chosen = dialog.SelectedPath;
                    if (File.Exists(Path.Combine(chosen, "settings.txt"))) return chosen;
                    string sub = Path.Combine(chosen, "Settings");
                    if (File.Exists(Path.Combine(sub, "settings.txt"))) return sub;

                    MessageBox.Show("所选文件夹中未找到有效的 settings.txt，将使用默认设置启动。",
                        "MicMuteBridge", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch { }
            return null;
        }

        private static bool CopyLegacyFolder(string source, string destination, params string[] files)
        {
            if (Directory.Exists(destination)) return false;
            string stage = destination + ".import-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(stage);
            try
            {
                bool copied = false;
                foreach (string name in files)
                    copied |= CopyIfPresent(Path.Combine(source, name), Path.Combine(stage, name));
                if (!copied) return false;
                // Publish the complete set together so settings cannot load before their sounds are copied.
                Directory.Move(stage, destination);
                return true;
            }
            finally
            {
                try { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
                catch { /* Preserve the original migration result. */ }
            }
        }

        private static bool CopyIfPresent(string source, string destination)
        {
            FileStream input;
            try { input = File.OpenRead(source); }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
            using (input)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    input.CopyTo(output);
            }
            return true;
        }
    }

    // Local, bounded diagnostics. Never records microphone audio or key presses.
    internal static class Diagnostics
    {
        public static string Folder { get { return LocalStorage.Current.LogsFolder; } }
        public static string CurrentFile { get { return Path.Combine(Folder, "MicMuteBridge.log"); } }
        private static readonly object Gate = new object();
        public static void Write(string line)
        {
            Append(Folder, "MicMuteBridge", line);
        }
        public static void WriteError(string line, string logName = "RUNTIME_ERROR")
        {
            Write(line);
            Append(LocalStorage.Current.ErrorsFolder, logName, line);
        }
        private static void Append(string folder, string logName, string line)
        {
            lock (Gate)
            {
                try
                {
                    Directory.CreateDirectory(folder);
                    string current = Path.Combine(folder, logName + ".log");
                    if (File.Exists(current) && new FileInfo(current).Length >= 1024 * 1024)
                    {
                        string old = Path.Combine(folder, logName + ".previous.log");
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(current, old);
                    }
                    File.AppendAllText(current, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + line + Environment.NewLine, Encoding.UTF8);
                }
                catch { /* Logging cannot interrupt audio control. */ }
            }
        }
    }

    internal static class SoundWarmup
    {
        public static readonly int[] Milliseconds = { 0, 100, 150, 200, 250, 300, 450, 650 };
        public static bool Contains(int ms)
        {
            foreach (int known in Milliseconds) if (known == ms) return true;
            return false;
        }
        public static int IndexOf(int ms)
        {
            for (int i = 0; i < Milliseconds.Length; ++i)
                if (Milliseconds[i] == ms) return i;
            return 5; // verified 300ms default
        }
    }

    // Settings store device IDs and preferences, including the chosen shortcut.
    internal sealed class Settings
    {
        private const string FormatKey = "settings_format";
        private const string FormatVersion = "1";
        private static readonly string[] Keys = {
            "mic_id", "bridge", "sound", "sound_mode", "sound_warmup_ms", "sound_volume_percent",
            "mute_trim_start", "mute_trim_end", "unmute_trim_start", "unmute_trim_end",
            "mute_preset", "unmute_preset", "all_mics"
        };

        public string MicId = "";
        public bool Bridge = true;
        public bool Sound = true;
        // Milliseconds of in-stream silence before sound (never delays microphone control).
        public int WarmupMs = 300;
        public int SoundVolumePercent = 100; // 0-100: applies to cue samples only, never system volume.
        public int MuteTrimStartMs = 0;
        public int MuteTrimEndMs = 0; // 0 means natural end of source.
        public int UnmuteTrimStartMs = 0;
        public int UnmuteTrimEndMs = 0;
        public string MutePreset = "classic";
        public string UnmutePreset = "classic";
        public bool FirstRun = true;
        public bool AllMics = true; // New installations: automatic microphone discovery; existing settings preserved.
        public DesktopShortcut DesktopKeys;

        public static string Folder { get { return LocalStorage.Current.SettingsFolder; } }

        public static string PathName { get { return Path.Combine(Folder, "settings.txt"); } }

        public static Settings Load()
        {
            return LoadFrom(PathName, Diagnostics.Write);
        }

        internal static Settings LoadFrom(string path, Action<string> log)
        {
            bool? currentValid, backupValid;
            Settings current = ReadSettings(path, log, out currentValid);
            if (currentValid == true) return current;

            Settings backup = ReadSettings(path + ".bak", log, out backupValid);
            if (backupValid == true)
            {
                log("已从 settings.txt.bak 读取上一份有效设置。");
                return backup;
            }
            if (currentValid.HasValue || backupValid.HasValue)
            {
                log("设置缺少有效备份，保留可读取的项目，其余项目使用默认值。");
                return current ?? backup ?? new Settings { FirstRun = false };
            }
            return new Settings();
        }

        private static string[] ReadLines(string path)
        {
            try { return File.ReadAllLines(path, Encoding.UTF8); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }

        private static Settings ReadSettings(string path, Action<string> log, out bool? valid)
        {
            valid = null; // null means the file is missing.
            try
            {
                string[] lines = ReadLines(path);
                if (lines == null) return null;
                bool complete;
                Settings settings = Parse(lines, out complete);
                valid = complete;
                if (!complete) log("设置文件不完整或有无效值：" + Path.GetFileName(path));
                return settings;
            }
            catch (Exception ex)
            {
                valid = false;
                log("读取设置失败 [" + Path.GetFileName(path) + "]：" + ex.Message);
                return null;
            }
        }

        private static Settings Parse(string[] lines, out bool valid)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            valid = true;
            foreach (string line in lines)
            {
                if (String.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#", StringComparison.Ordinal)) continue;
                int separator = line.IndexOf('=');
                if (separator <= 0) { valid = false; continue; }
                values[line.Substring(0, separator)] = line.Substring(separator + 1);
            }

            var settings = new Settings { FirstRun = false };
            string format;
            bool hasFormat = values.TryGetValue(FormatKey, out format);
            if (hasFormat && format != FormatVersion) valid = false;

            bool hasSetting = false;
            foreach (string key in Keys)
            {
                string value;
                if (!values.TryGetValue(key, out value))
                {
                    if (hasFormat) valid = false;
                    continue;
                }
                if (settings.Apply(key, value)) hasSetting = true;
                else valid = false;
            }
            valid = valid && hasSetting;
            string desktopKeys;
            if (values.TryGetValue("desktop_shortcut", out desktopKeys))
                settings.DesktopKeys = DesktopShortcut.Parse(desktopKeys);
            return hasSetting ? settings : null;
        }

        private bool Apply(string key, string value)
        {
            int number;
            switch (key)
            {
                case "mic_id":
                    try { MicId = Encoding.UTF8.GetString(Convert.FromBase64String(value)); return true; }
                    catch (FormatException) { return false; }
                case "bridge": return ReadFlag(value, ref Bridge);
                case "sound": return ReadFlag(value, ref Sound);
                case "all_mics": return ReadFlag(value, ref AllMics);
                case "sound_mode":
                    // Keys applies the legacy value first and then the explicit milliseconds.
                    if (!Int32.TryParse(value, out number)) return false;
                    WarmupMs = number == 0 ? 0 : number == 2 ? 650 : 300;
                    return true;
                case "sound_warmup_ms":
                    if (!Int32.TryParse(value, out number) || !SoundWarmup.Contains(number)) return false;
                    WarmupMs = number;
                    return true;
                case "sound_volume_percent": return ReadNumber(value, 100, ref SoundVolumePercent);
                case "mute_trim_start": return ReadNumber(value, 3000, ref MuteTrimStartMs);
                case "mute_trim_end": return ReadNumber(value, 3000, ref MuteTrimEndMs);
                case "unmute_trim_start": return ReadNumber(value, 3000, ref UnmuteTrimStartMs);
                case "unmute_trim_end": return ReadNumber(value, 3000, ref UnmuteTrimEndMs);
                case "mute_preset": return ReadPreset(value, ref MutePreset);
                case "unmute_preset": return ReadPreset(value, ref UnmutePreset);
                default: return false;
            }
        }

        private static bool ReadFlag(string value, ref bool target)
        {
            if (value != "0" && value != "1") return false;
            target = value == "1";
            return true;
        }

        private static bool ReadNumber(string value, int max, ref int target)
        {
            int number;
            if (!Int32.TryParse(value, out number) || number < 0 || number > max) return false;
            target = number;
            return true;
        }

        private static bool ReadPreset(string value, ref string target)
        {
            string preset = SoundCatalog.SafeId(value);
            if (!String.Equals(value, preset, StringComparison.OrdinalIgnoreCase)) return false;
            target = preset;
            return true;
        }

        public void Save()
        {
            try { SaveTo(PathName, Diagnostics.Write); }
            catch (Exception ex)
            {
                Diagnostics.WriteError("保存设置失败：" + ex.ToString());
                MessageBox.Show("保存设置失败，本次修改可能只在当前运行中生效。\n" + ex.Message,
                    "MicMuteBridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        internal void SaveTo(string path, Action<string> log)
        {
            string[] lines = {
                FormatKey + "=" + FormatVersion,
                "mic_id=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(MicId ?? "")),
                "bridge=" + (Bridge ? "1" : "0"),
                "sound=" + (Sound ? "1" : "0"),
                // Preserve legacy compatibility for rollback to v1.2.5.
                "sound_mode=" + (WarmupMs == 0 ? 0 : WarmupMs == 650 ? 2 : 1),
                "sound_warmup_ms=" + WarmupMs,
                "sound_volume_percent=" + SoundVolumePercent,
                "mute_trim_start=" + MuteTrimStartMs,
                "mute_trim_end=" + MuteTrimEndMs,
                "unmute_trim_start=" + UnmuteTrimStartMs,
                "unmute_trim_end=" + UnmuteTrimEndMs,
                "mute_preset=" + SoundCatalog.SafeId(MutePreset),
                "unmute_preset=" + SoundCatalog.SafeId(UnmutePreset),
                "all_mics=" + (AllMics ? "1" : "0"),
                "desktop_shortcut=" + DesktopKeys.Serialize()
            };

            string[] current = ReadLines(path);
            if (current != null && current.SequenceEqual(lines)) return;
            bool validCurrent = false;
            if (current != null) Parse(current, out validCurrent);

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, Encoding.UTF8))
                {
                    foreach (string line in lines) writer.WriteLine(line);
                    writer.Flush();
                    stream.Flush(true);
                }
                // Only replace the backup when the outgoing settings can be loaded.
                if (current == null) File.Move(temp, path);
                else File.Replace(temp, path, validCurrent ? path + ".bak" : null);
            }
            finally
            {
                try { File.Delete(temp); }
                catch (Exception ex) { log("清理设置临时文件失败：" + ex.Message); }
            }
        }
    }

    // Keep the existing record format for diagnostics and rollback. The record
    // describes a mute request, which may have been interrupted before completion.
    internal static class MuteSessionStore
    {
        public static string FileName { get { return Path.Combine(Settings.Folder, "mute-recovery.txt"); } }
        public static bool Load(Dictionary<string, bool> previous)
        {
            previous.Clear();
            if (!File.Exists(FileName)) return false;
            try
            {
                string[] lines = File.ReadAllLines(FileName, Encoding.UTF8);
                if (lines.Length == 0 || lines[0].Trim() != "MicMuteBridgeMuteSessionV2")
                    throw new InvalidDataException("Old or unrecognized recovery file");
                for (int i = 1; i < lines.Length; i++)
                {
                    string[] parts = lines[i].Split('|');
                    if (parts.Length != 2 || (parts[1] != "0" && parts[1] != "1"))
                        throw new InvalidDataException("Invalid recovery record");
                    string id = Encoding.UTF8.GetString(Convert.FromBase64String(parts[0]));
                    if (id.Length == 0 || previous.ContainsKey(id)) throw new InvalidDataException("Invalid device ID");
                    previous.Add(id, parts[1] == "1");
                }
                if (previous.Count == 0) throw new InvalidDataException("Empty recovery file");
                return true;
            }
            catch (Exception ex)
            {
                previous.Clear();
                Diagnostics.WriteError("恢复记录无效: " + ex.Message);
                return false;
            }
        }
        public static bool Save(Dictionary<string, bool> previous)
        {
            try
            {
                Directory.CreateDirectory(Settings.Folder);
                var rows = new List<string>();
                rows.Add("MicMuteBridgeMuteSessionV2");
                foreach (var entry in previous)
                    rows.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(entry.Key)) + "|" + (entry.Value ? "1" : "0"));
                string temp = FileName + ".tmp";
                File.WriteAllLines(temp, rows.ToArray(), Encoding.UTF8);
                if (File.Exists(FileName)) File.Replace(temp, FileName, null);
                else File.Move(temp, FileName);
                return true;
            }
            catch (Exception ex)
            {
                Diagnostics.WriteError("保存恢复记录失败: " + ex.ToString());
                return false;
            }
        }
        public static bool Delete()
        {
            try { if (File.Exists(FileName)) File.Delete(FileName); return true; }
            catch (Exception ex) { Diagnostics.WriteError("删除恢复记录失败: " + ex.ToString()); return false; }
        }
    }

    // The bundled sounds are synthesized for this project, not third-party samples.
    internal static class SoundCatalog
    {
        public static readonly string[] Ids = { "classic", "soft", "pixel", "chime", "bubble", "digital", "custom" };
        public static readonly string[] Names = {
            "经典提示音（原版）", "柔和气泡", "像素按键", "清脆铃音",
            "水滴弹跳", "电子科技", "自定义 WAV（本机保存）" };
        public static string SafeId(string id)
        {
            if (id != null) foreach (string known in Ids)
                if (String.Equals(known, id, StringComparison.OrdinalIgnoreCase)) return known;
            return "classic";
        }
        public static string FileName(string id, bool muted)
        {
            return SafeId(id) + (muted ? "_mute.wav" : "_unmute.wav");
        }
        public static string CustomPath(bool muted)
        {
            return Path.Combine(Settings.Folder, "Sounds", FileName("custom", muted));
        }
        public static byte[] ReadCue(string id, bool muted)
        {
            id = SafeId(id);
            if (id == "custom") return WavPrep.ReadFile(CustomPath(muted));

            string name = FileName(id, muted);
            using (Stream resource = typeof(SoundCatalog).Assembly.GetManifestResourceStream(name))
            {
                if (resource == null)
                    throw new InvalidDataException("程序缺少内置音效 " + name + "。请完整解压新版 ZIP，再双击 START.cmd。");
                using (var copy = new MemoryStream())
                {
                    resource.CopyTo(copy);
                    return copy.ToArray();
                }
            }
        }
        public static string LabelOf(string id)
        {
            for (int i = 0; i < Ids.Length; i++) if (Ids[i] == SafeId(id)) return Names[i];
            return Names[0];
        }
    }

    internal sealed class PresetItem
    {
        public string Id;
        public string Display;
        public PresetItem(string id, string display) { Id = id; Display = display; }
        public override string ToString() { return Display; }
    }

    // Reads uncompressed mono/stereo PCM or float WAV and converts the audio to
    // 44.1 kHz stereo PCM16, with silent lead-in in the SAME Windows audio stream.
    // No external codecs or packages, and nothing blocks microphone operation.
    internal static class WavPrep
    {
        // Build() always yields RIFF PCM16 stereo with a canonical 44-byte header.
        // Scale only the sample bytes, never the stream header, fade timing or lead-in length.
        public static byte[] ApplyVolume(byte[] wav, int percent)
        {
            if (percent < 0 || percent > 100) throw new ArgumentOutOfRangeException("percent");
            if (percent == 100) return wav;
            if (wav == null || wav.Length < 44 || (wav.Length - 44) % 2 != 0)
                throw new InvalidDataException("Unexpected prepared WAV layout");
            for (int i = 44; i < wav.Length; i += 2)
            {
                short sample = (short)(wav[i] | (wav[i + 1] << 8));
                int scaled = (int)Math.Round(sample * (percent / 100.0));
                short result = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, scaled));
                wav[i] = (byte)(result & 0xff);
                wav[i + 1] = (byte)((result >> 8) & 0xff);
            }
            return wav;
        }

        public static byte[] Build(string source, int leadMs, int tailMs)
        {
            return Build(ReadFile(source), leadMs, tailMs, 0, 0);
        }

        public static int DurationMs(byte[] source)
        {
            // Shares format validation with the real playback path.
            byte[] parsed = Build(source, 0, 0, 0, 0);
            return (int)Math.Round((parsed.Length - 44) * 1000.0 / (44100 * 4));
        }

        public static float[] Peaks(byte[] source, int count)
        {
            byte[] parsed = Build(source, 0, 0, 0, 0);
            int frames = (parsed.Length - 44) / 4;
            var peaks = new float[count];
            for (int i = 0; i < count; i++)
            {
                int a = (int)((long)i * frames / count);
                int b = Math.Max(a + 1, (int)((long)(i + 1) * frames / count));
                int stride = Math.Max(1, (b - a) / 32);
                int max = 0;
                for (int j = a; j < b; j += stride)
                {
                    int offset = 44 + j * 4;
                    int l = Math.Abs((int)BitConverter.ToInt16(parsed, offset));
                    int r = Math.Abs((int)BitConverter.ToInt16(parsed, offset + 2));
                    max = Math.Max(max, Math.Max(l, r));
                }
                peaks[i] = Math.Min(1f, max / 32768f);
            }
            return peaks;
        }

        public static byte[] ReadFile(string source)
        {
            var info = new FileInfo(source);
            if (!info.Exists) throw new FileNotFoundException("找不到音效文件", source);
            if (info.Length > 10 * 1024 * 1024) throw new InvalidDataException("WAV 不能大于 10MB");
            return File.ReadAllBytes(source);
        }

        public static byte[] Build(byte[] raw, int leadMs, int tailMs, int startMs, int endMs)
        {
            ushort format = 0, channels = 0, bits = 0;
            int rate = 0, dataOffset = -1, dataLength = 0;
            using (var ms = new MemoryStream(raw, false))
            using (var r = new BinaryReader(ms))
            {
                if (ms.Length < 44 || Encoding.ASCII.GetString(r.ReadBytes(4)) != "RIFF")
                    throw new InvalidDataException("文件不是标准 RIFF WAV");
                r.ReadUInt32();
                if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "WAVE")
                    throw new InvalidDataException("文件不是 WAVE 音频");
                while (ms.Position + 8 <= ms.Length)
                {
                    string id = Encoding.ASCII.GetString(r.ReadBytes(4));
                    uint length = r.ReadUInt32();
                    long next = ms.Position + (long)length + (length % 2);
                    if (next > ms.Length) throw new InvalidDataException("WAV 数据块超出文件范围");
                    if (id == "fmt " && length >= 16)
                    {
                        format = r.ReadUInt16(); channels = r.ReadUInt16();
                        rate = r.ReadInt32(); r.ReadUInt32(); r.ReadUInt16(); bits = r.ReadUInt16();
                    }
                    else if (id == "data")
                    {
                        dataOffset = checked((int)ms.Position);
                        dataLength = checked((int)length);
                    }
                    ms.Position = next;
                }
            }
            if (format != 1 && format != 3)
                throw new InvalidDataException("仅支持未压缩 WAV：PCM 或 32-bit Float。MP3/ADPCM 请先转换为 WAV");
            if (channels != 1 && channels != 2) throw new InvalidDataException("仅支持单声道或双声道 WAV");
            if (rate < 8000 || rate > 192000) throw new InvalidDataException("WAV 采样率超出支持范围");
            if ((format == 1 && bits != 8 && bits != 16 && bits != 24 && bits != 32) ||
                (format == 3 && bits != 32)) throw new InvalidDataException("不支持这个 WAV 位深度");
            int bytesPerSample = bits / 8;
            int frameSize = channels * bytesPerSample;
            if (dataOffset < 0 || dataLength < frameSize) throw new InvalidDataException("WAV 缺少音频数据");
            int frames = dataLength / frameSize;
            if (frames < rate / 100) throw new InvalidDataException("音效太短（小于 10ms）");
            if ((double)frames / rate > 3.0) throw new InvalidDataException("音效时长不能超过 3 秒");
            if (leadMs < 0 || leadMs > 2000 || tailMs < 0 || tailMs > 2000)
                throw new ArgumentOutOfRangeException("leadMs", "预热或结尾静音超出范围");
            if (startMs < 0 || endMs < 0 || startMs > 3000 || endMs > 3000)
                throw new InvalidDataException("裁剪时间超出范围");
            int startFrame = (int)Math.Round((double)startMs * rate / 1000);
            int endFrame = endMs == 0 ? frames : (int)Math.Round((double)endMs * rate / 1000);
            if (startFrame < 0 || endFrame > frames || endFrame - startFrame < rate * 0.020)
                throw new InvalidDataException("裁剪后的声音至少需要 20ms，且结束时间不能超过原音效时长");
            bool trimmed = startFrame != 0 || endFrame != frames;
            const int targetRate = 44100;
            int outFrames = Math.Max(1, (int)Math.Round((double)(endFrame - startFrame) * targetRate / rate));
            int lead = targetRate * leadMs / 1000, tail = targetRate * tailMs / 1000;
            int totalFrames = checked(outFrames + lead + tail);
            using (var output = new MemoryStream())
            using (var w = new BinaryWriter(output))
            {
                int bytes = checked(totalFrames * 4);
                w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + bytes);
                w.Write(Encoding.ASCII.GetBytes("WAVE"));
                w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16);
                w.Write((short)1); w.Write((short)2); w.Write(targetRate);
                w.Write(targetRate * 4); w.Write((short)4); w.Write((short)16);
                w.Write(Encoding.ASCII.GetBytes("data")); w.Write(bytes);
                for (int i = 0; i < lead; i++) { w.Write((short)0); w.Write((short)0); }
                for (int i = 0; i < outFrames; i++)
                {
                    double src = startFrame + i * (double)rate / targetRate;
                    int a = Math.Min(endFrame - 1, (int)src), b = Math.Min(endFrame - 1, a + 1);
                    double f = src - a;
                    for (int ch = 0; ch < 2; ch++)
                    {
                        int srcCh = channels == 1 ? 0 : ch;
                        double p = ReadSample(raw, dataOffset + a * frameSize + srcCh * bytesPerSample, format, bits);
                        double q = ReadSample(raw, dataOffset + b * frameSize + srcCh * bytesPerSample, format, bits);
                        double v = Math.Max(-1, Math.Min(1, p + (q - p) * f));
                        // Fade only edited cuts, avoiding clicks without touching the
                        // familiar full-length preset waveform.
                        if (trimmed)
                        {
                            int fadeFrames = Math.Min(targetRate / 200, outFrames / 3); // up to 5 ms
                            if (fadeFrames > 0)
                            {
                                double fadeIn = Math.Min(1.0, i / (double)fadeFrames);
                                double fadeOut = Math.Min(1.0, (outFrames - i - 1) / (double)fadeFrames);
                                v *= Math.Max(0, Math.Min(fadeIn, fadeOut));
                            }
                        }
                        w.Write((short)Math.Round(v * 32760));
                    }
                }
                for (int i = 0; i < tail; i++) { w.Write((short)0); w.Write((short)0); }
                w.Flush(); return output.ToArray();
            }
        }
        private static double ReadSample(byte[] a, int pos, ushort format, ushort bits)
        {
            if (format == 3) return BitConverter.ToSingle(a, pos);
            if (bits == 8) return (a[pos] - 128) / 128.0;
            if (bits == 16) return BitConverter.ToInt16(a, pos) / 32768.0;
            if (bits == 24)
            {
                int v = a[pos] | (a[pos + 1] << 8) | (a[pos + 2] << 16);
                if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
                return v / 8388608.0;
            }
            return BitConverter.ToInt32(a, pos) / 2147483648.0;
        }
    }

    // All warmup settings use the SAME source WAV samples and trim positions.
    // Only an initial silent PCM lead-in varies: the mic toggle never waits.
    internal sealed class TonePlayer : IDisposable
    {
        private readonly SoundPlayer[,] players = new SoundPlayer[SoundWarmup.Milliseconds.Length, 2];
        private readonly MemoryStream[,] streams = new MemoryStream[SoundWarmup.Milliseconds.Length, 2];
        private readonly string[] chosen = { "classic", "classic" };
        private readonly int[] clipStarts = new int[2];
        private readonly int[] clipEnds = new int[2];
        private readonly int gainPercent;
        private bool disposed;
        public string MutePreset { get { return chosen[0]; } }
        public string UnmutePreset { get { return chosen[1]; } }
        public int MuteStart { get { return clipStarts[0]; } }
        public int MuteEnd { get { return clipEnds[0]; } }
        public int UnmuteStart { get { return clipStarts[1]; } }
        public int UnmuteEnd { get { return clipEnds[1]; } }

        public TonePlayer(string mute, string unmute, int muteStart, int muteEnd,
            int unmuteStart, int unmuteEnd, int volumePercent)
        {
            if (volumePercent < 0 || volumePercent > 100) throw new ArgumentOutOfRangeException("volumePercent");
            gainPercent = volumePercent;
            try
            {
                LoadInitial(true, mute, muteStart, muteEnd);
                LoadInitial(false, unmute, unmuteStart, unmuteEnd);
            }
            catch { Dispose(); throw; }
        }
        private void LoadInitial(bool muted, string preset, int startMs, int endMs)
        {
            try { SetCue(muted, preset, startMs, endMs); }
            catch (Exception ex)
            {
                Diagnostics.WriteError((muted ? "闭麦" : "开麦") + "音效或裁剪配置加载失败，使用完整声音：" + ex.Message);
                try { SetCue(muted, preset, 0, 0); }
                catch { SetCue(muted, "classic", 0, 0); }
            }
        }
        public void SetCue(bool muted, string preset, int startMs, int endMs)
        {
            if (disposed) throw new ObjectDisposedException("TonePlayer");
            preset = SoundCatalog.SafeId(preset);
            int kind = muted ? 0 : 1;
            var newPlayers = new SoundPlayer[SoundWarmup.Milliseconds.Length];
            var newStreams = new MemoryStream[SoundWarmup.Milliseconds.Length];
            try
            {
                byte[] source = SoundCatalog.ReadCue(preset, muted);
                for (int index = 0; index < SoundWarmup.Milliseconds.Length; index++)
                {
                    int lead = SoundWarmup.Milliseconds[index];
                    byte[] wav = WavPrep.ApplyVolume(WavPrep.Build(source, lead, 90, startMs, endMs), gainPercent);
                    newStreams[index] = new MemoryStream(wav, false);
                    newPlayers[index] = new SoundPlayer(newStreams[index]);
                    newPlayers[index].Load();
                }
            }
            catch
            {
                for (int i = 0; i < newPlayers.Length; i++)
                {
                    if (newPlayers[i] != null) newPlayers[i].Dispose();
                    if (newStreams[i] != null) newStreams[i].Dispose();
                }
                throw;
            }
            for (int index = 0; index < SoundWarmup.Milliseconds.Length; index++)
            {
                // Commit only after every new player has loaded successfully.
                if (players[index, kind] != null) players[index, kind].Dispose();
                if (streams[index, kind] != null) streams[index, kind].Dispose();
                players[index, kind] = newPlayers[index];
                streams[index, kind] = newStreams[index];
            }
            chosen[kind] = preset;
            clipStarts[kind] = startMs;
            clipEnds[kind] = endMs;
        }
        public void Play(bool nowMuted, int warmupMs)
        {
            if (disposed) return;
            int index = SoundWarmup.IndexOf(warmupMs);
            try { players[index, nowMuted ? 0 : 1].Play(); }
            catch (Exception ex)
            {
                Diagnostics.WriteError("音效播放请求失败 [" + (nowMuted ? "闭麦" : "开麦") +
                    ", warmup=" + warmupMs + "ms]: " + ex.ToString());
            }
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            for (int index = 0; index < SoundWarmup.Milliseconds.Length; index++)
                for (int kind = 0; kind < 2; kind++)
                {
                    try { if (players[index, kind] != null) players[index, kind].Dispose(); } catch { }
                    try { if (streams[index, kind] != null) streams[index, kind].Dispose(); } catch { }
                }
        }
    }

    internal static class TrayIcons
    {
        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);

        public static Icon Make(int kind)
        {
            // kind: 0 = on, 1 = all muted, 2 = unknown, 3 = partial or partial failure.
            Color fill = kind == 0 ? Color.FromArgb(32, 149, 93) :
                kind == 1 ? Color.FromArgb(205, 65, 69) :
                kind == 3 ? Color.FromArgb(221, 144, 22) : Color.FromArgb(112, 118, 126);
            using (var bmp = new Bitmap(32, 32))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (Brush bg = new SolidBrush(fill)) g.FillEllipse(bg, 1, 1, 30, 30);
                using (Brush white = new SolidBrush(Color.White))
                using (Pen p = new Pen(Color.White, 2.3F))
                {
                    p.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                    p.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                    g.FillEllipse(white, 13, 7, 6, 12);
                    g.DrawArc(p, 10, 11, 12, 12, 0, 180);
                    g.DrawLine(p, 16, 23, 16, 26);
                    g.DrawLine(p, 12, 26, 20, 26);
                }
                if (kind != 0)
                {
                    using (Pen slashBorder = new Pen(fill, 5F))
                    using (Pen slash = new Pen(Color.White, 2.8F))
                    {
                        slashBorder.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                        slashBorder.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                        slash.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                        slash.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                        g.DrawLine(slashBorder, 8, 25, 24, 7);
                        g.DrawLine(slash, 8, 25, 24, 7);
                    }
                }
                IntPtr h = bmp.GetHicon();
                try { return (Icon)Icon.FromHandle(h).Clone(); }
                finally { DestroyIcon(h); }
            }
        }
    }

    internal struct DesktopShortcut
    {
        private const string ChordPrefix = "chord:";
        public const uint Alt = 1, Control = 2, Shift = 4;
        public readonly uint Modifiers;
        public readonly Keys Key;
        public readonly DesktopChord Chord;
        public bool Enabled { get { return Chord != null || Key != Keys.None; } }

        private DesktopShortcut(uint modifiers, Keys key)
        {
            Modifiers = modifiers;
            Key = key;
            Chord = null;
        }

        public DesktopShortcut(DesktopChord chord)
        {
            Modifiers = 0;
            Key = Keys.None;
            Chord = chord;
        }

        public static bool Supports(Keys key)
        {
            return (key >= Keys.A && key <= Keys.Z) ||
                (key >= Keys.D0 && key <= Keys.D9) ||
                (key >= Keys.F1 && key <= Keys.F11) ||
                key == Keys.Home || key == Keys.End || key == Keys.Insert ||
                key == Keys.PageUp || key == Keys.PageDown || key == Keys.Scroll;
        }

        public static bool TryCreate(uint modifiers, Keys key, out DesktopShortcut result)
        {
            result = default(DesktopShortcut);
            if (modifiers == 0 || (modifiers & ~(Alt | Control | Shift)) != 0 || !Supports(key)) return false;
            result = new DesktopShortcut(modifiers, key);
            return true;
        }

        public static DesktopShortcut Parse(string value)
        {
            if (String.IsNullOrEmpty(value)) return default(DesktopShortcut);
            if (value.StartsWith(ChordPrefix, StringComparison.Ordinal))
                return new DesktopShortcut(DesktopChord.Parse(value.Substring(ChordPrefix.Length)));
            uint modifiers;
            int key;
            DesktopShortcut result;
            string[] parts = value.Split(',');
            if (parts.Length == 2 && UInt32.TryParse(parts[0], out modifiers) &&
                Int32.TryParse(parts[1], out key) && TryCreate(modifiers, (Keys)key, out result)) return result;
            return default(DesktopShortcut);
        }

        public string Serialize()
        {
            if (Chord != null) return ChordPrefix + Chord.Serialize();
            return Enabled ? Modifiers + "," + (int)Key : "";
        }

        public static string KeyLabel(Keys key)
        {
            if (key >= Keys.D0 && key <= Keys.D9) return ((int)key - (int)Keys.D0).ToString();
            if (key == Keys.PageUp) return "Page Up";
            if (key == Keys.PageDown) return "Page Down";
            if (key == Keys.Scroll) return "Scroll Lock";
            return key.ToString();
        }

        public override string ToString()
        {
            if (Chord != null) return Chord.ToString();
            if (!Enabled) return "已关闭";
            string prefix = "";
            if ((Modifiers & Control) != 0) prefix += "Ctrl+";
            if ((Modifiers & Alt) != 0) prefix += "Alt+";
            if ((Modifiers & Shift) != 0) prefix += "Shift+";
            return prefix + KeyLabel(Key);
        }
    }

    internal sealed class DesktopChord
    {
        public const ushort MouseBack = 0x0101, MouseForward = 0x0102;
        private readonly ushort[] keys;

        private DesktopChord(IEnumerable<ushort> source)
        {
            keys = source.OrderBy(key => key).ToArray();
        }

        public static DesktopChord Create(HashSet<ushort> held)
        {
            if (held.Count == 0 || held.Any(key => !Supports(key))) return null;
            return new DesktopChord(held);
        }

        private static bool Supports(ushort key)
        {
            if (key == MouseBack || key == MouseForward) return true;
            int prefix = key & 0xFF00, make = key & 0xFF;
            return (prefix == 0 || prefix == 0xE000) && make > 0 && make <= 0x7F && key != GameKeyMap.VolumeMute;
        }

        public static DesktopChord Parse(string value)
        {
            var held = new HashSet<ushort>();
            foreach (string part in value.Split('+'))
            {
                ushort key;
                if (part.Length != 4 || !UInt16.TryParse(part, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out key) || !held.Add(key))
                    return null;
            }
            return Create(held);
        }

        public string Serialize()
        {
            return String.Join("+", keys.Select(key => key.ToString("X4", CultureInfo.InvariantCulture)).ToArray());
        }

        public bool UsesMouse { get { return keys.Contains(MouseBack) || keys.Contains(MouseForward); } }
        public bool Contains(ushort key) { return keys.Contains(key); }
        public bool Matches(HashSet<ushort> held) { return held.SetEquals(keys); }
        public bool Overlaps(IEnumerable<ushort> held) { return keys.Any(held.Contains); }

        public override string ToString()
        {
            return String.Join(" + ", keys.Select(KeyLabel).ToArray());
        }

        private static string KeyLabel(ushort code)
        {
            if (code == MouseBack) return "鼠标侧键 1";
            if (code == MouseForward) return "鼠标侧键 2";
            GameKeyOption key = GameKeyMap.FromScanCode(code);
            return key == null ? "键位 0x" + code.ToString("X4", CultureInfo.InvariantCulture) : key.ToString();
        }
    }

    internal sealed class DesktopChordActivation
    {
        private bool triggered;
        public DesktopChord Chord { get; private set; }

        public void Set(DesktopChord chord, IEnumerable<ushort> held)
        {
            Chord = chord;
            triggered = chord != null && chord.Overlaps(held);
        }

        public bool Update(HashSet<ushort> held, bool pressed)
        {
            if (Chord == null) return false;
            if (triggered)
            {
                if (!Chord.Overlaps(held)) triggered = false;
                return false;
            }
            if (!pressed || !Chord.Matches(held)) return false;
            triggered = true;
            return true;
        }
    }

    internal sealed class DesktopChordRecording
    {
        public const string Instructions = "同时按住想用的键或鼠标侧键，全部松开后完成录制。";
        private bool releasing;
        public bool Invalid { get; private set; }
        public DesktopChord Candidate { get; private set; }

        public DesktopChord Update(HashSet<ushort> held, bool pressed)
        {
            if (held.Count == 0)
            {
                DesktopChord result = Invalid ? null : Candidate;
                Reset();
                return result;
            }
            if (!pressed) releasing = true;
            else if (!Invalid)
            {
                Candidate = releasing ? null : DesktopChord.Create(held);
                Invalid = Candidate == null;
            }
            return null;
        }

        public void Reset()
        {
            releasing = false;
            Invalid = false;
            Candidate = null;
        }
    }

    internal sealed class DesktopHotKey : NativeWindow, IDisposable
    {
        private const int WmHotKey = 0x0312;
        private const uint NoRepeat = 0x4000;
        private readonly Action onPressed;
        private int activeId;
        public DesktopShortcut Active { get; private set; }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr window, int id);

        public DesktopHotKey(Action pressed)
        {
            onPressed = pressed;
            CreateHandle(new CreateParams());
        }

        public bool TrySet(DesktopShortcut shortcut, out string problem)
        {
            problem = "";
            if (shortcut.Modifiers == Active.Modifiers && shortcut.Key == Active.Key) return true;

            int newId = activeId == 1 ? 2 : 1;
            if (shortcut.Key != Keys.None && !RegisterHotKey(Handle, newId, shortcut.Modifiers | NoRepeat, (uint)shortcut.Key))
            {
                int error = Marshal.GetLastWin32Error();
                problem = error == 1409 ? "这个组合键已被 Windows 或其他程序使用，请换一个。" :
                    "Windows 无法注册组合键。错误 " + error + "，" + new Win32Exception(error).Message;
                return false;
            }
            if (activeId != 0 && !UnregisterHotKey(Handle, activeId))
            {
                int error = Marshal.GetLastWin32Error();
                if (shortcut.Key != Keys.None) UnregisterHotKey(Handle, newId);
                problem = "Windows 无法释放原组合键。错误 " + error + "，" + new Win32Exception(error).Message;
                return false;
            }
            activeId = shortcut.Key != Keys.None ? newId : 0;
            Active = shortcut;
            return true;
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WmHotKey && activeId != 0 && message.WParam.ToInt32() == activeId)
            {
                long keyData = message.LParam.ToInt64();
                if ((keyData & 7) == Active.Modifiers && (Keys)((keyData >> 16) & 0xFFFF) == Active.Key)
                    onPressed();
                return;
            }
            base.WndProc(ref message);
        }

        public void Dispose()
        {
            if (activeId != 0) UnregisterHotKey(Handle, activeId);
            activeId = 0;
            Active = default(DesktopShortcut);
            if (Handle != IntPtr.Zero) DestroyHandle();
        }
    }

    internal sealed class DesktopRawInput : NativeWindow, IDisposable
    {
        private const int WmInput = 0x00FF, WmInputDeviceChange = 0x00FE;
        private const uint ReadInput = 0x10000003;
        private const uint Remove = 0x00000001, InputSink = 0x00000100, DeviceNotify = 0x00002000;
        private const int MouseInput = 0, KeyboardInput = 1;
        private const int KeyBreak = 1, KeyE0 = 2, KeyE1 = 4;
        private const int MouseBackDown = 0x0040, MouseBackUp = 0x0080;
        private const int MouseForwardDown = 0x0100, MouseForwardUp = 0x0200;
        private static readonly int[] KeypadNavigation = {
            (int)Keys.Insert, (int)Keys.End, (int)Keys.Down, (int)Keys.PageDown, (int)Keys.Left,
            (int)Keys.Clear, (int)Keys.Right, (int)Keys.Home, (int)Keys.Up, (int)Keys.PageUp
        };
        private readonly HashSet<ushort> held = new HashSet<ushort>();
        private readonly Dictionary<IntPtr, HashSet<ushort>> devices = new Dictionary<IntPtr, HashSet<ushort>>();
        private readonly Dictionary<ushort, int> virtualKeys = new Dictionary<ushort, int>();
        private readonly DesktopChordActivation activation = new DesktopChordActivation();
        private readonly HashSet<ushort> mappedMuteKeys = new HashSet<ushort>();
        private readonly System.Windows.Forms.Timer releaseTimer = new System.Windows.Forms.Timer { Interval = 40 };
        private readonly Action onPressed;
        private readonly Action<string> logError;
        private readonly uint headerSize = (uint)(8 + IntPtr.Size * 2);
        private IntPtr buffer;
        private bool registered, mouseRegistered, inputErrorLogged, waitingForRelease;
        private DesktopChordRecording recording;
        private Func<bool> recordingFocused;
        private Action<DesktopChord, string> recordingChanged;
        private bool paused;

        [StructLayout(LayoutKind.Sequential)]
        private struct RawInputDevice
        {
            public ushort UsagePage;
            public ushort Usage;
            public uint Flags;
            public IntPtr Target;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint count, uint size);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetRawInputData(IntPtr input, uint command, IntPtr data, ref uint size, uint headerSize);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int key);

        public DesktopRawInput(Action pressed, Action<string> error)
        {
            onPressed = pressed;
            logError = error;
            CreateHandle(new CreateParams());
            try { buffer = Marshal.AllocHGlobal((int)headerSize + 24); }
            catch { DestroyHandle(); throw; }
            releaseTimer.Tick += (s,e) =>
            {
                if (AnyKeyDown()) return;
                WaitForRelease(false);
                if (recordingChanged != null) recordingChanged(null, DesktopChordRecording.Instructions);
            };
        }

        private static RawInputDevice Device(ushort usage, uint flags, IntPtr target)
        {
            return new RawInputDevice { UsagePage = 1, Usage = usage, Flags = flags, Target = target };
        }

        private static void Register(RawInputDevice[] entries)
        {
            if (!RegisterRawInputDevices(entries, (uint)entries.Length, (uint)Marshal.SizeOf(typeof(RawInputDevice))))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        private void SetRegistration(bool keyboard, bool mouse)
        {
            var entries = new List<RawInputDevice>();
            uint flags = InputSink | DeviceNotify;
            if (keyboard != registered) entries.Add(Device(6, keyboard ? flags : Remove, keyboard ? Handle : IntPtr.Zero));
            if (mouse != mouseRegistered) entries.Add(Device(2, mouse ? flags : Remove, mouse ? Handle : IntPtr.Zero));
            if (entries.Count != 0) Register(entries.ToArray());
            registered = keyboard;
            mouseRegistered = mouse;
        }

        public void SetChord(DesktopChord chord)
        {
            SetRegistration(chord != null, chord != null && chord.UsesMouse);
            activation.Set(chord, held);
            ResetInput();
        }

        public void SetMappedMuteKeys(IEnumerable<ushort> keys)
        {
            mappedMuteKeys.Clear();
            mappedMuteKeys.UnionWith(keys);
        }

        public bool UsesMappedMuteKey(DesktopChord chord)
        {
            return chord.Overlaps(mappedMuteKeys);
        }

        public void SetPaused(bool value)
        {
            paused = value;
            if (!paused && registered) ResetInput();
        }

        public void StartRecording(Func<bool> focused, Action<DesktopChord, string> changed)
        {
            SetRegistration(true, true);
            recording = new DesktopChordRecording();
            recordingFocused = focused;
            recordingChanged = changed;
            ResetInput();
        }

        public void DiscardRecording()
        {
            if (recording != null) ResetInput();
        }

        public void StopRecording()
        {
            recording = null;
            recordingFocused = null;
            recordingChanged = null;
            if (buffer == IntPtr.Zero) return;
            DesktopChord chord = activation.Chord;
            SetRegistration(chord != null, chord != null && chord.UsesMouse);
            ResetInput();
        }

        private void ResetInput()
        {
            devices.Clear();
            held.Clear();
            virtualKeys.Clear();
            activation.Set(activation.Chord, held);
            if (recording != null) recording.Reset();
            // A held key's first report after registration can be an auto-repeat.
            WaitForRelease(registered && AnyKeyDown());
        }

        private void WaitForRelease(bool value)
        {
            waitingForRelease = value;
            releaseTimer.Enabled = value;
            if (value && recordingChanged != null)
                recordingChanged(null, "请先松开已经按住的键，再录制。");
        }

        private bool AnyKeyDown()
        {
            for (int key = mouseRegistered ? 5 : 8; key <= 0xFE; key++)
                if (GetAsyncKeyState(key) < 0) return true;
            return false;
        }

        private static bool IsKeyDown(int key)
        {
            if (GetAsyncKeyState(key) < 0) return true;
            if (key >= (int)Keys.NumPad0 && key <= (int)Keys.NumPad9)
                return GetAsyncKeyState(KeypadNavigation[key - (int)Keys.NumPad0]) < 0;
            return key == (int)Keys.Decimal && GetAsyncKeyState((int)Keys.Delete) < 0;
        }

        private void ChangeKey(IntPtr device, ushort key, bool pressed, int virtualKey)
        {
            if (waitingForRelease) return;
            HashSet<ushort> keys;
            if (!devices.TryGetValue(device, out keys))
            {
                if (!pressed) return;
                keys = new HashSet<ushort>();
                devices.Add(device, keys);
            }
            if (pressed)
            {
                if (!keys.Add(key) || !held.Add(key)) return;
                virtualKeys[key] = virtualKey;
            }
            else
            {
                if (!keys.Remove(key)) return;
                if (keys.Count == 0) devices.Remove(device);
                if (devices.Values.Any(other => other.Contains(key))) return;
                held.Remove(key);
                virtualKeys.Remove(key);
            }

            if (recording != null)
            {
                DesktopChord result = recording.Update(held, pressed);
                recordingChanged(result, recording.Invalid ? (held.Contains(GameKeyMap.VolumeMute) ?
                    "扬声器静音键用于上方游戏单键，请松开后换一组。" : "请松开所有键，再同时按住要录制的组合。") :
                    recording.Candidate != null ? recording.Candidate.ToString() : DesktopChordRecording.Instructions);
            }
            else if (pressed && !paused && activation.Chord != null && activation.Chord.Contains(key) &&
                virtualKeys.Any(pair => pair.Key != key && pair.Value != 0 && !IsKeyDown(pair.Value)))
            {
                // Discard stale state if an earlier key-up was missed, for example during a desktop switch.
                ResetInput();
            }
            else if (activation.Update(held, pressed) && !paused) onPressed();
        }

        private static int PhysicalVirtualKey(ushort scan, int key)
        {
            switch (scan)
            {
                case 0x002A: return (int)Keys.LShiftKey;
                case 0x0036: return (int)Keys.RShiftKey;
                case 0x001D: return (int)Keys.LControlKey;
                case 0xE01D: return (int)Keys.RControlKey;
                case 0x0038: return (int)Keys.LMenu;
                case 0xE038: return (int)Keys.RMenu;
                case 0x0053: return (int)Keys.Decimal;
            }
            // With Num Lock off, keypad digits carry navigation virtual keys.
            if (scan >= 0x0047 && scan <= 0x0052)
            {
                int keypadIndex = Array.IndexOf(KeypadNavigation, key);
                if (keypadIndex >= 0) return (int)Keys.NumPad0 + keypadIndex;
            }
            return key;
        }

        private void ReadMessage(IntPtr input)
        {
            uint size = headerSize + 24;
            uint read = GetRawInputData(input, ReadInput, buffer, ref size, headerSize);
            if (read == UInt32.MaxValue)
            {
                int error = Marshal.GetLastWin32Error();
                ResetInput();
                if (!inputErrorLogged)
                {
                    inputErrorLogged = true;
                    logError("读取常规快捷键输入失败：" + new Win32Exception(error).Message);
                }
                return;
            }
            if (read < headerSize + 16) return;
            int kind = Marshal.ReadInt32(buffer);
            IntPtr device = Marshal.ReadIntPtr(buffer, 8);
            int body = (int)headerSize;
            if (kind == KeyboardInput)
            {
                ushort make = (ushort)Marshal.ReadInt16(buffer, body);
                int flags = Marshal.ReadInt16(buffer, body + 2);
                int key = (ushort)Marshal.ReadInt16(buffer, body + 6);
                if (make == 0xFF)
                {
                    ResetInput();
                    if (recordingChanged != null) recordingChanged(null, "键盘无法同时上报这些键，请全部松开后换一组。");
                    return;
                }
                ushort scan = (ushort)(make | ((flags & KeyE0) != 0 ? 0xE000 : 0));
                if (key >= 0xFF || scan == 0xE02A || scan == 0xE036) return;
                if (key == (int)Keys.VolumeMute) scan = GameKeyMap.VolumeMute;
                else if ((flags & KeyE1) != 0 || key == (int)Keys.Pause || key == (int)Keys.Cancel ||
                    key == (int)Keys.Packet || make == 0 || make > 0x7F)
                {
                    if (recording != null)
                    {
                        ResetInput();
                        recordingChanged(null, "这个键没有可用的按下和松开信号，请换一组。");
                    }
                    return;
                }
                else if (key == (int)Keys.NumLock) scan = 0x0045;
                if (mappedMuteKeys.Contains(scan)) scan = GameKeyMap.VolumeMute;
                ChangeKey(device, scan, (flags & KeyBreak) == 0, PhysicalVirtualKey(scan, key));
            }
            else if (kind == MouseInput && mouseRegistered && read >= headerSize + 24)
            {
                int buttons = (ushort)Marshal.ReadInt16(buffer, body + 4);
                if ((buttons & MouseBackDown) != 0) ChangeKey(device, DesktopChord.MouseBack, true, (int)Keys.XButton1);
                if ((buttons & MouseBackUp) != 0) ChangeKey(device, DesktopChord.MouseBack, false, (int)Keys.XButton1);
                if ((buttons & MouseForwardDown) != 0) ChangeKey(device, DesktopChord.MouseForward, true, (int)Keys.XButton2);
                if ((buttons & MouseForwardUp) != 0) ChangeKey(device, DesktopChord.MouseForward, false, (int)Keys.XButton2);
            }
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WmInput)
            {
                try
                {
                    if (buffer != IntPtr.Zero && registered && (recordingFocused == null || recordingFocused()))
                        ReadMessage(message.LParam);
                }
                finally { base.WndProc(ref message); }
                return;
            }
            if (message.Msg == WmInputDeviceChange && message.WParam.ToInt64() == 2 && devices.Remove(message.LParam))
            {
                ResetInput();
                if (recording != null) recordingChanged(null, "输入设备已断开，请松开后重新录制。");
            }
            base.WndProc(ref message);
        }

        public void Dispose()
        {
            recording = null;
            recordingFocused = null;
            recordingChanged = null;
            try { SetRegistration(false, false); }
            catch (Exception ex) { logError("关闭常规快捷键输入失败：" + ex.Message); }
            releaseTimer.Dispose();
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
                buffer = IntPtr.Zero;
            }
            if (Handle != IntPtr.Zero) DestroyHandle();
        }
    }

    internal sealed class GameKeyOption
    {
        public readonly string Name;
        public readonly ushort ScanCode;
        private readonly string label;

        public GameKeyOption(string name, ushort scanCode, string displayLabel = null)
        {
            Name = name;
            ScanCode = scanCode;
            label = displayLabel ?? name;
        }

        public override string ToString() { return label; }
    }

    internal static class GameKeyMap
    {
        public const ushort VolumeMute = 0xE020;
        private const string RecordedPrefix = "Scan_";
        private const int ExtendedKey = 1 << 24;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int GetKeyNameTextW(int keyData, StringBuilder name, int length);

        public static readonly GameKeyOption[] Choices = {
            new GameKeyOption("Home", 0xE047), new GameKeyOption("End", 0xE04F),
            new GameKeyOption("Insert", 0xE052), new GameKeyOption("ScrollLock", 0x0046, "Scroll Lock"),
            new GameKeyOption("PageUp", 0xE049), new GameKeyOption("PageDown", 0xE051),
            new GameKeyOption("F1", 0x003B), new GameKeyOption("F2", 0x003C), new GameKeyOption("F3", 0x003D),
            new GameKeyOption("F4", 0x003E), new GameKeyOption("F5", 0x003F), new GameKeyOption("F6", 0x0040),
            new GameKeyOption("F7", 0x0041), new GameKeyOption("F8", 0x0042), new GameKeyOption("F9", 0x0043),
            new GameKeyOption("F10", 0x0044), new GameKeyOption("F11", 0x0057), new GameKeyOption("F12", 0x0058),
            new GameKeyOption("Numpad0", 0x0052, "小键盘 0"), new GameKeyOption("Numpad1", 0x004F, "小键盘 1"),
            new GameKeyOption("Numpad2", 0x0050, "小键盘 2"), new GameKeyOption("Numpad3", 0x0051, "小键盘 3"),
            new GameKeyOption("Numpad4", 0x004B, "小键盘 4"), new GameKeyOption("Numpad5", 0x004C, "小键盘 5"),
            new GameKeyOption("Numpad6", 0x004D, "小键盘 6"), new GameKeyOption("Numpad7", 0x0047, "小键盘 7"),
            new GameKeyOption("Numpad8", 0x0048, "小键盘 8"), new GameKeyOption("Numpad9", 0x0049, "小键盘 9"),
            new GameKeyOption("NumpadMultiply", 0x0037, "小键盘 *"), new GameKeyOption("NumpadDivide", 0xE035, "小键盘 /"),
            new GameKeyOption("NumpadAdd", 0x004E, "小键盘 +"), new GameKeyOption("NumpadSubtract", 0x004A, "小键盘 -"),
            new GameKeyOption("NumpadDecimal", 0x0053, "小键盘 ."), new GameKeyOption("NumpadEnter", 0xE01C, "小键盘 Enter")
        };

        public static ushort ScanCodeFromMessage(int keyData)
        {
            // WM_KEYDOWN/UP carry the scan code in bits 16–23 and the E0 flag in bit 24.
            int scanCode = (keyData >> 16) & 0xFF;
            if ((keyData & ExtendedKey) != 0) scanCode |= 0xE000;
            return (ushort)scanCode;
        }

        public static ushort RecordableScanCode(Keys key, int keyData)
        {
            if (key == Keys.Pause || key == Keys.Cancel || key == Keys.Packet || key == Keys.VolumeMute) return 0;
            ushort scanCode = ScanCodeFromMessage(keyData);
            // Legacy key messages use E045 for Num Lock and 0045 for Pause.
            if (scanCode == 0x0045 || scanCode == 0xE045)
                return key == Keys.NumLock ? (ushort)0x0045 : (ushort)0;
            // Korean language keys use 0xF1/0xF2 in legacy key-up messages.
            if (scanCode == 0x00F1 || scanCode == 0x00F2) scanCode = (ushort)(scanCode & 0x007F);
            return CanMap(scanCode) ? scanCode : (ushort)0;
        }

        public static bool CanCaptureOnRelease(ushort scanCode)
        {
            return scanCode == 0xE037 || scanCode == 0x0071 || scanCode == 0x0072;
        }

        private static bool CanMap(ushort scanCode)
        {
            int makeCode = scanCode & 0xFF;
            int prefix = scanCode & 0xFF00;
            // SysRq and Break are reported for Alt+Print Screen and Ctrl+Pause.
            return makeCode > 0 && makeCode <= 0x7F && (prefix == 0 || prefix == 0xE000) &&
                scanCode != 0x0054 && scanCode != 0xE045 && scanCode != 0xE046 && scanCode != VolumeMute;
        }

        public static GameKeyOption FromScanCode(ushort scanCode)
        {
            GameKeyOption preset = Choices.FirstOrDefault(choice => choice.ScanCode == scanCode);
            if (preset != null) return preset;
            if (!CanMap(scanCode)) return null;

            string code = scanCode.ToString("X4", CultureInfo.InvariantCulture);
            int keyData = (scanCode & 0xFF) << 16;
            // GetKeyNameText uses the legacy extended-key name table for Num Lock.
            if ((scanCode & 0xFF00) == 0xE000 || scanCode == 0x0045) keyData |= ExtendedKey;
            var name = new StringBuilder(80);
            string label = GetKeyNameTextW(keyData, name, name.Capacity) > 0 ? name.ToString() : "键位 0x" + code;
            return new GameKeyOption(RecordedPrefix + code, scanCode, label);
        }

        public static bool TryGetScanCode(string name, out ushort scanCode)
        {
            GameKeyOption preset = Choices.FirstOrDefault(choice => String.Equals(choice.Name, name, StringComparison.OrdinalIgnoreCase));
            if (preset != null)
            {
                scanCode = preset.ScanCode;
                return true;
            }

            ushort parsed;
            if (name != null && name.Length == RecordedPrefix.Length + 4 && name.StartsWith(RecordedPrefix, StringComparison.OrdinalIgnoreCase) &&
                ushort.TryParse(name.Substring(RecordedPrefix.Length), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out parsed) &&
                CanMap(parsed))
            {
                scanCode = parsed;
                return true;
            }
            scanCode = 0;
            return false;
        }

        public static uint MuteEntry(ushort source)
        {
            return ((uint)source << 16) | VolumeMute;
        }

        public static List<uint> Decode(byte[] bytes)
        {
            var entries = new List<uint>();
            if (bytes == null) return entries;
            if (bytes.Length < 16 || BitConverter.ToUInt32(bytes, 0) != 0 || BitConverter.ToUInt32(bytes, 4) != 0 ||
                12 + 4L * BitConverter.ToUInt32(bytes, 8) != bytes.Length ||
                BitConverter.ToUInt32(bytes, bytes.Length - 4) != 0)
                throw new InvalidDataException("Windows 里已有的键盘映射格式无法识别，已保留原设置。");
            var sources = new HashSet<ushort>();
            for (int offset = 12; offset < bytes.Length - 4; offset += 4)
            {
                uint entry = BitConverter.ToUInt32(bytes, offset);
                ushort source = (ushort)(entry >> 16);
                if (source == 0 || !sources.Add(source))
                    throw new InvalidDataException("Windows 映射里有无效或重复的键，已保留原设置。");
                entries.Add(entry);
            }
            return entries;
        }

        public static byte[] Encode(IList<uint> entries)
        {
            var bytes = new byte[16 + 4 * entries.Count];
            Array.Copy(BitConverter.GetBytes((uint)(entries.Count + 1)), 0, bytes, 8, 4);
            for (int index = 0; index < entries.Count; index++)
                Array.Copy(BitConverter.GetBytes(entries[index]), 0, bytes, 12 + 4 * index, 4);
            return bytes;
        }

        public static List<uint> Plan(IList<uint> entries, HashSet<ushort> owned, ushort? requested)
        {
            var updated = new List<uint>();
            bool foundRequested = false;
            foreach (uint entry in entries)
            {
                ushort source = (ushort)(entry >> 16);
                bool ownedMute = owned.Contains(source) && entry == MuteEntry(source);
                if (requested == source)
                {
                    if (!ownedMute)
                        throw new InvalidOperationException("该键已有其他系统映射。请换一个键，或在原来的工具里先处理。");
                    foundRequested = true;
                    updated.Add(entry);
                }
                else if (!ownedMute) updated.Add(entry);
            }
            if (requested.HasValue && !foundRequested) updated.Add(MuteEntry(requested.Value));
            return updated;
        }
    }

    internal static class GameKeySetup
    {
        private const string SetCommand = "--set-game-key";
        private const string UndoCommand = "--undo-game-key";
        private const string KeyboardPath = @"SYSTEM\CurrentControlSet\Control\Keyboard Layout";
        private const string OwnerPath = @"SOFTWARE\MicMuteBridge\HomeMuteMap";
        private const string MapName = "Scancode Map";
        private const string LegacyMarker = "OriginalMapPresent";
        private const string MarkerPrefix = "GameKey_";

        private sealed class Marker
        {
            public string Name;
            public ushort ScanCode;
            public bool OriginalMapPresent;
        }

        private static RegistryKey OpenMachine()
        {
            return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, Environment.Is64BitOperatingSystem ?
                RegistryView.Registry64 : RegistryView.Registry32);
        }

        private static byte[] ReadMap(RegistryKey keyboard)
        {
            if (!keyboard.GetValueNames().Contains(MapName, StringComparer.OrdinalIgnoreCase)) return null;
            byte[] bytes = keyboard.GetValue(MapName) as byte[];
            if (keyboard.GetValueKind(MapName) != RegistryValueKind.Binary || bytes == null)
                throw new InvalidDataException("Windows 键盘映射的类型无法识别。");
            return bytes;
        }

        internal static bool TryGetMarkerScanCode(string name, out ushort source)
        {
            if (String.Equals(name, LegacyMarker, StringComparison.OrdinalIgnoreCase))
            {
                source = 0xE047;
                return true;
            }
            if (name != null && name.StartsWith(MarkerPrefix, StringComparison.OrdinalIgnoreCase))
                return GameKeyMap.TryGetScanCode(name.Substring(MarkerPrefix.Length), out source);
            source = 0;
            return false;
        }

        private static List<Marker> ReadMarkers(RegistryKey owner)
        {
            var markers = new List<Marker>();
            if (owner == null) return markers;
            foreach (string name in owner.GetValueNames())
            {
                ushort source;
                if (!TryGetMarkerScanCode(name, out source)) continue;
                object value = owner.GetValue(name);
                if (value == null) continue;
                if (!(value is int) || ((int)value != 0 && (int)value != 1))
                    throw new InvalidDataException("键位的撤回记录无法识别。");
                markers.Add(new Marker { Name = name, ScanCode = source, OriginalMapPresent = (int)value == 1 });
            }
            return markers;
        }

        public static GameKeyOption[] SavedKeys()
        {
            using (RegistryKey machine = OpenMachine())
            using (RegistryKey keyboard = machine.OpenSubKey(KeyboardPath))
            using (RegistryKey owner = machine.OpenSubKey(OwnerPath))
            {
                if (keyboard == null || owner == null) return new GameKeyOption[0];
                List<uint> entries = GameKeyMap.Decode(ReadMap(keyboard));
                var owned = new HashSet<ushort>(ReadMarkers(owner).Select(item => item.ScanCode));
                var saved = new List<GameKeyOption>();
                foreach (uint entry in entries)
                {
                    ushort source = (ushort)(entry >> 16);
                    if (owned.Contains(source) && entry == GameKeyMap.MuteEntry(source))
                        saved.Add(GameKeyMap.FromScanCode(source));
                }
                return saved.ToArray();
            }
        }

        public static ushort[] MappedMuteKeys()
        {
            using (RegistryKey machine = OpenMachine())
            using (RegistryKey keyboard = machine.OpenSubKey(KeyboardPath))
            {
                if (keyboard == null) return new ushort[0];
                return GameKeyMap.Decode(ReadMap(keyboard))
                    .Where(entry => (ushort)entry == GameKeyMap.VolumeMute)
                    .Select(entry => (ushort)(entry >> 16))
                    .ToArray();
            }
        }

        public static void Launch(GameKeyOption option, IWin32Window window)
        {
            try
            {
                var info = new ProcessStartInfo(Application.ExecutablePath, option == null ? UndoCommand : SetCommand + " " + option.Name);
                info.UseShellExecute = true;
                info.Verb = "runas";
                using (Process worker = Process.Start(info)) { }
            }
            catch (Exception ex)
            {
                var systemError = ex as Win32Exception;
                if (systemError != null && systemError.NativeErrorCode == 1223) return;
                Diagnostics.WriteError("启动系统单键设置失败：" + ex.ToString(), "KEY_MAPPING_ERROR");
                MessageBox.Show(window, ex.Message, "快捷键设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        public static bool HandleCommand(string[] args)
        {
            if (args.Length == 0 || (args[0] != SetCommand && args[0] != UndoCommand)) return false;
            try
            {
                GameKeyOption option = null;
                ushort scanCode;
                if (args[0] == SetCommand && args.Length == 2 && GameKeyMap.TryGetScanCode(args[1], out scanCode))
                    option = GameKeyMap.FromScanCode(scanCode);
                if ((args[0] == SetCommand && option == null) || (args[0] == UndoCommand && args.Length != 1))
                    throw new ArgumentException("请在快捷键设置里录制或选择一个可用的系统单键。");
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                        throw new UnauthorizedAccessException("请在快捷键设置里同意 Windows 的管理员授权。");
                }
                MessageBox.Show(Update(option), "快捷键设置", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Diagnostics.WriteError("系统单键设置失败：" + ex.ToString(), "KEY_MAPPING_ERROR");
                MessageBox.Show(ex.Message, "快捷键设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            return true;
        }

        private static string Update(GameKeyOption option)
        {
            using (var gate = new Mutex(false, @"Global\MicMuteBridge-HomeMuteMap"))
            {
                bool locked;
                try { locked = gate.WaitOne(5000); }
                catch (AbandonedMutexException) { locked = true; }
                if (!locked) throw new InvalidOperationException("另一项键位设置正在执行，请稍后再试。");
                try
                {
                    using (RegistryKey machine = OpenMachine())
                    using (RegistryKey keyboard = machine.OpenSubKey(KeyboardPath, true))
                    {
                        if (keyboard == null) throw new IOException("无法打开 Windows 键盘设置。");
                        byte[] oldBytes = ReadMap(keyboard);
                        List<uint> entries = GameKeyMap.Decode(oldBytes);
                        List<Marker> markers;
                        using (RegistryKey previous = machine.OpenSubKey(OwnerPath))
                            markers = ReadMarkers(previous);
                        if (option == null && markers.Count == 0) return "暂无本工具写入的系统单键。";
                        var owned = new HashSet<ushort>(markers.Select(marker => marker.ScanCode));
                        bool originalPresent = markers.Count == 0 ? oldBytes != null : markers.All(marker => marker.OriginalMapPresent);
                        ushort? requested = option == null ? (ushort?)null : option.ScanCode;
                        List<uint> updated = GameKeyMap.Plan(entries, owned, requested);
                        string keepMarker = option == null ? null : MarkerPrefix + option.Name;
                        using (RegistryKey owner = option == null ? machine.OpenSubKey(OwnerPath, true) : machine.CreateSubKey(OwnerPath))
                        {
                            if (owner == null) throw new IOException("无法打开键位的撤回记录。");
                            // Write ownership before changing the keyboard map so undo can also recover a partial update.
                            if (keepMarker != null)
                            {
                                owner.SetValue(keepMarker, originalPresent ? 1 : 0, RegistryValueKind.DWord);
                                owner.Flush();
                            }
                            byte[] newBytes = updated.Count != 0 || (oldBytes != null && originalPresent) ?
                                GameKeyMap.Encode(updated) : null;
                            if (!EqualMap(oldBytes, newBytes))
                            {
                                if (newBytes == null) keyboard.DeleteValue(MapName, false);
                                else keyboard.SetValue(MapName, newBytes, RegistryValueKind.Binary);
                                keyboard.Flush();
                                if (!EqualMap(ReadMap(keyboard), newBytes))
                                    throw new IOException("保存后读回的键位与预期不一致。");
                            }
                            foreach (Marker marker in markers)
                                if (!String.Equals(marker.Name, keepMarker, StringComparison.OrdinalIgnoreCase))
                                    owner.DeleteValue(marker.Name, false);
                            owner.Flush();
                        }
                        return option == null ? "已撤回本工具设置的系统单键。请重启 Windows。" :
                            "已将 " + option + " 设置为扬声器静音。请重启 Windows，再用 MicMuteBridge 测试。";
                    }
                }
                finally { gate.ReleaseMutex(); }
            }
        }

        private static bool EqualMap(byte[] first, byte[] second)
        {
            if (first == null || second == null) return first == second;
            return first.SequenceEqual(second);
        }
    }

    internal sealed class BridgeContext : ApplicationContext
    {
        private const int AudioRetryIntervalMs = 2000;
        private const string AudioWaitingText = "等待 Windows 音频";
        private readonly Settings cfg;
        private readonly Stopwatch audioRetryWait = new Stopwatch();
        private AudioSystem audio;
        private IAudioEndpointVolume output;
        private IAudioEndpointVolume mic;
        private string activeOutputId = "";
        private string activeOutputName = "尚未连接";
        private string activeMicName = "未选择";
        private bool outputLastMuted;
        private bool? micMuted;
        private bool exiting;
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly ContextMenuStrip menu = new ContextMenuStrip();
        private readonly ToolStripMenuItem muteMenu = new ToolStripMenuItem();
        private readonly ToolStripMenuItem soundMenu = new ToolStripMenuItem("播放切麦提示音");
        // Track devices handled by a mute request in this process for hotplug.
        private readonly Dictionary<string, bool> previousAllStates = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> knownAllIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<MicStateEntry> currentMicStates = new List<MicStateEntry>();
        private bool allMuteSession;
        private readonly HashSet<string> unmuteRetryIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private int allMicFailures;
        private int allMicGood;
        private int allMicMutedCount;
        private string allMicStatus = "未检查";
        private SettingsForm window;
        private SoundLibraryForm soundWindow;
        private DesktopHotKey desktopHotKey;
        private DesktopRawInput desktopInput;
        private bool desktopPaused;
        private Icon iconOn, iconOff, iconMissing, iconPartial;
        private TonePlayer sound;
        private int tickCount;
        private string lastProblem = "";
        private readonly List<string> logLines = new List<string>();
        private int conversions;
        public event EventHandler Changed;

        public BridgeContext()
        {
            cfg = Settings.Load();
            if (MuteSessionStore.Load(previousAllStates))
                Diagnostics.Write("发现上次闭麦记录，仅供诊断。启动时只读取麦克风状态。");
            previousAllStates.Clear();
            iconOn = TrayIcons.Make(0);
            iconOff = TrayIcons.Make(1);
            iconMissing = TrayIcons.Make(2);
            iconPartial = TrayIcons.Make(3);
            try
            {
                sound = new TonePlayer(cfg.MutePreset, cfg.UnmutePreset,
                    cfg.MuteTrimStartMs, cfg.MuteTrimEndMs, cfg.UnmuteTrimStartMs, cfg.UnmuteTrimEndMs,
                    cfg.SoundVolumePercent);
                if (cfg.MutePreset != sound.MutePreset || cfg.UnmutePreset != sound.UnmutePreset ||
                    cfg.MuteTrimStartMs != sound.MuteStart || cfg.MuteTrimEndMs != sound.MuteEnd ||
                    cfg.UnmuteTrimStartMs != sound.UnmuteStart || cfg.UnmuteTrimEndMs != sound.UnmuteEnd)
                {
                    cfg.MutePreset = sound.MutePreset;
                    cfg.UnmutePreset = sound.UnmutePreset;
                    cfg.MuteTrimStartMs = sound.MuteStart;
                    cfg.MuteTrimEndMs = sound.MuteEnd;
                    cfg.UnmuteTrimStartMs = sound.UnmuteStart;
                    cfg.UnmuteTrimEndMs = sound.UnmuteEnd;
                    cfg.Save();
                }
            }
            catch (Exception ex)
            {
                sound = null;
                Diagnostics.WriteError("音效播放器初始化失败：" + ex.ToString());
            }

            muteMenu.Click += (s,e) => ToggleMic("托盘右键菜单");
            menu.Items.Add(muteMenu);
            soundMenu.Click += (s,e) => SetSound(!cfg.Sound);
            menu.Items.Add(soundMenu);
            menu.Items.Add(new ToolStripMenuItem("快捷键设置…", null, (s,e) => OpenShortcutSettings()));
            menu.Items.Add(new ToolStripMenuItem("详细设置…", null, (s,e) => OpenSettings()));
            menu.Items.Add(new ToolStripMenuItem("退出", null, (s,e) => ExitBridge()));
            menu.Opening += (s,e) => RefreshMenu();

            tray.Icon = iconMissing;
            tray.Text = "MicMuteBridge " + Program.Version + " - " + AudioWaitingText;
            tray.ContextMenuStrip = menu;
            tray.MouseClick += (s,e) => { if (e.Button == MouseButtons.Left) ToggleMic("托盘左键"); };
            tray.Visible = true;

            Log("MicMuteBridge " + Program.Version + " 已启动。输出自动跟随 Windows 默认设备。模式：" + (cfg.AllMics ? "全部可用麦克风" : "指定麦克风") + "。", false);
            Log("OS=" + Environment.OSVersion.VersionString + ", 64bit=" + Environment.Is64BitOperatingSystem + ", logs=" + Diagnostics.CurrentFile, false);
            Log("音效预热=" + cfg.WarmupMs + "ms，音效音量=" + cfg.SoundVolumePercent + "%" + "；闭麦音效=" + cfg.MutePreset + "；开麦音效=" + cfg.UnmutePreset, false);
            if (cfg.FirstRun) Log("首次使用：默认自动控制全部活动麦克风。可在详细设置中改为指定设备。", false);
            RefreshDevices();
            if (cfg.DesktopKeys.Enabled)
            {
                string problem;
                if (!TryApplyDesktopShortcut(cfg.DesktopKeys, out problem))
                    LogError("常规快捷键启动失败：" + problem);
            }

            timer.Interval = 40;
            timer.Tick += OnTick;
            timer.Start();
            UpdateUi();
            // First run needs an explicit mic target; subsequent launches stay in tray,
            // even if Mix 01 is temporarily disconnected during Windows sign-in.
            if (cfg.FirstRun)
            {
                cfg.FirstRun = false;
                cfg.Save();
                OpenSettings();
            }
            else if (audio != null && mic == null && !cfg.AllMics)
                tray.ShowBalloonTip(3500, "MicMuteBridge", "指定麦克风暂时不可用，软件将自动等待设备恢复。", ToolTipIcon.Info);
        }

        private static string Error(Exception ex)
        {
            return ex.GetType().Name + " HRESULT=0x" + ex.HResult.ToString("X8") + " " + ex.Message;
        }
        public bool AllMicsOn { get { return cfg.AllMics; } }
        public string AllMicStatus { get { return audio == null ? AudioWaitingText : allMicStatus; } }
        public string MicDetails
        {
            get
            {
                if (audio == null) return AudioWaitingText;
                if (!cfg.AllMics)
                    return activeMicName + "    " + (!micMuted.HasValue ? "不可访问" : (micMuted.Value ? "已静音" : "已开启"));
                if (currentMicStates.Count == 0) return "未发现可用的录音设备。";
                var rows = new List<string>();
                foreach (MicStateEntry item in currentMicStates)
                {
                    string status = !item.Muted.HasValue ? "[异常]" : item.Muted.Value ? "[静音]" : "[开启]";
                    rows.Add(status + "  " + item.Name + (String.IsNullOrEmpty(item.Problem) ? "" : "  |  " + item.Problem));
                }
                return String.Join(Environment.NewLine, rows.ToArray());
            }
        }
        public bool BridgeOn { get { return cfg.Bridge; } }
        public bool SoundOn { get { return cfg.Sound; } }
        public bool CanRequestAllMicUnmute { get { return cfg.AllMics && allMicGood > 0; } }
        public string OutputName { get { return audio == null ? AudioWaitingText : activeOutputName; } }
        public string MicName { get { return audio == null ? AudioWaitingText : activeMicName; } }
        public string Problem { get { return lastProblem; } }
        public int Conversions { get { return conversions; } }
        public bool? MicMuted { get { return micMuted; } }
        public string SavedMicId { get { return cfg.MicId; } }
        public bool StartupOn { get { return IsStartupOn(); } }
        public string Logs { get { return String.Join(Environment.NewLine, logLines.ToArray()); } }

        public List<AudioDevice> GetCaptureDevices()
        {
            if (audio == null) return new List<AudioDevice>();
            try { return audio.List(EDataFlow.Capture); }
            catch (Exception ex) { LogError("刷新麦克风列表失败：" + Error(ex)); return new List<AudioDevice>(); }
        }

        private void FreeOutput()
        {
            if (output != null) { try { Marshal.ReleaseComObject(output); } catch { } output = null; }
            activeOutputId = "";
        }

        private void FreeMic()
        {
            if (mic != null) { try { Marshal.ReleaseComObject(mic); } catch { } mic = null; }
            micMuted = null;
        }

        private string FindName(EDataFlow flow, string id)
        {
            try
            {
                foreach (AudioDevice device in audio.List(flow))
                    if (device.Id == id) return device.Name;
            }
            catch { }
            return id == null ? "不可用" : "未命名的音频设备";
        }

        private void RebindOutput()
        {
            if (audio == null) return;
            const string NoDefaultOutput = "未找到默认扬声器";
            string wanted = audio.DefaultId(EDataFlow.Render, ERole.Multimedia);
            if (wanted == activeOutputId && output != null) return;
            if (String.IsNullOrEmpty(wanted) && output == null && activeOutputName == NoDefaultOutput) return;
            FreeOutput();
            activeOutputName = NoDefaultOutput;
            if (String.IsNullOrEmpty(wanted))
            {
                Log("当前没有可用的 Windows 默认输出设备。");
                UpdateUi();
                return;
            }
            try
            {
                output = audio.Volume(wanted);
                activeOutputId = wanted;
                activeOutputName = FindName(EDataFlow.Render, wanted);
                output.GetMute(out outputLastMuted); // baseline: never trigger on a device switch
                Log("已自动切换监听：" + activeOutputName + (outputLastMuted ? "（当前系统已静音，请先自行恢复）" : ""));
            }
            catch (Exception ex)
            {
                FreeOutput();
                activeOutputName = "默认扬声器连接失败";
                Log("无法监听默认扬声器：" + Error(ex));
            }
            UpdateUi();
        }

        private void RebindMic()
        {
            FreeMic();
            activeMicName = "未选择麦克风";
            if (audio == null || String.IsNullOrEmpty(cfg.MicId)) { UpdateUi(); return; }
            try
            {
                mic = audio.Volume(cfg.MicId);
                activeMicName = FindName(EDataFlow.Capture, cfg.MicId);
                bool state;
                mic.GetMute(out state);
                micMuted = state;
                Log("麦克风已连接：" + activeMicName);
            }
            catch (Exception ex)
            {
                activeMicName = "指定麦克风不可用（不会自动选择其他麦克风）";
                Log("麦克风断开：" + Error(ex));
            }
            UpdateUi();
        }

        public void SelectMic(string id)
        {
            cfg.MicId = id ?? "";
            cfg.FirstRun = false;
            cfg.Save();
            if (!cfg.AllMics) RebindMic();
        }

        public void SetAllMics(bool enabled)
        {
            if (cfg.AllMics == enabled) return;
            // Mode changes end the current all-microphone operation.
            if (allMuteSession || unmuteRetryIds.Count > 0)
            {
                var answer = MessageBox.Show("当前存在未结束的全部麦克风切换。\n切换模式会清理恢复记录，保留麦克风当前状态。\n确定要继续吗？", "静音安全提示", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (answer != DialogResult.Yes) { UpdateUi(); return; }
                if (!MuteSessionStore.Delete())
                {
                    MessageBox.Show("无法删除静音恢复记录，已取消模式切换。请查看错误日志。", "MicMuteBridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    UpdateUi(); return;
                }
                Log("切换模式：用户确认清理闭麦记录，保留设备当前状态。");
            }
            allMuteSession = false;
            unmuteRetryIds.Clear();
            previousAllStates.Clear();
            knownAllIds.Clear();
            cfg.AllMics = enabled;
            cfg.FirstRun = false;
            cfg.Save();
            if (enabled)
            {
                FreeMic();
                RefreshAllMics();
            }
            else
            {
                currentMicStates.Clear();
                RebindMic();
            }
            Log("麦克风控制模式：" + (enabled ? "全部可用录音设备" : "指定麦克风"));
            UpdateUi();
        }

        private void RefreshAllMics(bool applySessionMute = false)
        {
            if (!cfg.AllMics || audio == null) return;
            try
            {
                int failures;
                List<AudioDevice> devices = audio.List(EDataFlow.Capture, out failures);
                var visibleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var previousIds = new HashSet<string>(knownAllIds, StringComparer.OrdinalIgnoreCase);
                int muted = 0, good = 0;
                var details = new List<MicStateEntry>();
                foreach (AudioDevice d in devices)
                {
                    IAudioEndpointVolume v = null;
                    var detail = new MicStateEntry { Id = d.Id, Name = d.Name };
                    details.Add(detail);
                    try
                    {
                        v = audio.Volume(d.Id);
                        bool state;
                        v.GetMute(out state);
                        if (applySessionMute && allMuteSession && !previousAllStates.ContainsKey(d.Id))
                        {
                            // Keep a record of devices handled during this live mute session.
                            previousAllStates[d.Id] = state;
                            if (!MuteSessionStore.Save(previousAllStates))
                            {
                                previousAllStates.Remove(d.Id);
                                throw new IOException("无法保存新设备的恢复状态，因此拒绝改变其静音状态");
                            }
                            if (!state)
                            {
                                v.SetMute(true, IntPtr.Zero);
                                v.GetMute(out state);
                                Log("静音会话发现新设备：" + d.Name + (state ? "，已静音" : "，静音失败"));
                            }
                        }
                        detail.Muted = state;
                        good++;
                        visibleIds.Add(d.Id);
                        if (!previousIds.Contains(d.Id))
                            Log("发现录音设备：" + d.Name + "，状态=" + (state ? "静音" : "开启"));
                        if (state) muted++;
                    }
                    catch (Exception ex)
                    {
                        failures++;
                        detail.Problem = Error(ex);
                        ReportOnce("录音设备读取/切换失败 " + d.Name + "：" + detail.Problem);
                    }
                    finally { if (v != null) Marshal.ReleaseComObject(v); }
                }
                currentMicStates.Clear();
                currentMicStates.AddRange(details);
                foreach (string id in previousIds)
                    if (!visibleIds.Contains(id)) Log("录音设备暂时不可访问：ID=" + id);
                knownAllIds.Clear();
                foreach (string id in visibleIds) knownAllIds.Add(id);
                allMicGood = good;
                allMicFailures = failures;
                allMicMutedCount = muted;
                // Status is based on successfully read endpoints. Problems are separately flagged.
                micMuted = good == 0 ? (bool?)null : muted == good;
                activeMicName = "自动管理活动麦克风（" + good + "/" + (good + failures) + "）";
                allMicStatus = failures > 0 ? "部分设备出错（" + failures + " 台；正常 " + good + " 台）" :
                    good == 0 ? "没有可访问的活动麦克风" :
                    muted == good ? "全部静音（" + muted + " 台）" :
                    muted == 0 ? "全部开启（" + good + " 台）" :
                    "部分静音（" + muted + "/" + good + " 台）";
                if (failures == 0) lastProblem = "";
            }
            catch (Exception ex)
            {
                currentMicStates.Clear();
                micMuted = null;
                allMicGood = 0;
                allMicFailures = 1;
                allMicMutedCount = 0;
                allMicStatus = "录音设备枚举失败";
                ReportOnce("枚举录音设备失败：" + Error(ex));
            }
            UpdateUi();
        }

        private bool ToggleAllMics(string cause, bool explicitUnmute = false)
        {
            if (audio == null) return false;
            try
            {
                int skipped;
                List<AudioDevice> devices = audio.List(EDataFlow.Capture, out skipped);
                if (devices.Count == 0 && skipped == 0)
                {
                    Log("未发现活动录音设备，未执行操作。");
                    RefreshAllMics();
                    return false;
                }
                var states = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                foreach (AudioDevice d in devices)
                {
                    IAudioEndpointVolume v = null;
                    try
                    {
                        v = audio.Volume(d.Id);
                        bool state;
                        v.GetMute(out state);
                        states[d.Id] = state;
                    }
                    catch (Exception ex) { LogError("读取状态失败 [" + d.Name + "]：" + Error(ex)); }
                    finally { if (v != null) Marshal.ReleaseComObject(v); }
                }
                if (states.Count == 0)
                {
                    Log("所有活动麦克风均不可访问，未执行操作。");
                    RefreshAllMics();
                    return false;
                }

                int readFailures = skipped + devices.Count - states.Count;
                int muted = 0;
                foreach (bool isMuted in states.Values) if (isMuted) muted++;
                AllMicAction action = AllMicToggleDecision.Decide(states.Count, muted, readFailures,
                    states.Keys, unmuteRetryIds, explicitUnmute);
                if (action == AllMicAction.Hold)
                {
                    Log(cause + "：有 " + readFailures + " 项录音设备状态无法确认，已保留现有静音。请查看麦克风明细。");
                    RefreshAllMics();
                    return false;
                }

                bool targetMuted = action == AllMicAction.Mute;
                Log(cause + "：可读 " + states.Count + " 台，状态未知 " + readFailures +
                    " 项，目标=" + (targetMuted ? "全部静音" : "全部开麦"));
                unmuteRetryIds.Clear();
                if (targetMuted)
                {
                    if (!allMuteSession) previousAllStates.Clear();
                    foreach (var state in states)
                        if (!previousAllStates.ContainsKey(state.Key)) previousAllStates.Add(state.Key, state.Value);
                    if (!MuteSessionStore.Save(previousAllStates))
                        Log("闭麦记录保存失败，本次继续闭麦。");
                    allMuteSession = true;
                }
                else
                {
                    // Suspend hotplug muting while the user is opening these devices.
                    allMuteSession = false;
                    unmuteRetryIds.UnionWith(states.Keys);
                }

                int succeeded = 0;
                int failed = readFailures;
                int unchanged = 0;
                foreach (AudioDevice d in devices)
                {
                    bool before;
                    if (!states.TryGetValue(d.Id, out before)) continue;
                    if (before == targetMuted)
                    {
                        unchanged++;
                        succeeded++;
                        Log("设备已符合目标状态 [" + d.Name + "]：" + (before ? "已静音" : "已开启"));
                        continue;
                    }
                    IAudioEndpointVolume v = null;
                    try
                    {
                        v = audio.Volume(d.Id);
                        v.SetMute(targetMuted, IntPtr.Zero);
                        bool actual;
                        v.GetMute(out actual);
                        if (actual == targetMuted)
                        {
                            succeeded++;
                            Log("设备状态切换成功 [" + d.Name + "]：" + (before ? "静音" : "开启") + " -> " + (actual ? "静音" : "开启"));
                            continue;
                        }
                        LogError("设备切换未生效 [" + d.Name + "]：期望" + (targetMuted ? "静音" : "开启") + "，实际" + (actual ? "静音" : "开启"));
                    }
                    catch (Exception ex)
                    {
                        LogError("设备切换异常 [" + d.Name + "]：" + Error(ex));
                    }
                    finally { if (v != null) Marshal.ReleaseComObject(v); }
                    failed++;
                    // Continue closing other microphones, but stop opening after a failure.
                    if (!targetMuted) break;
                }

                RefreshAllMics(targetMuted);
                bool complete = failed == 0 && allMicFailures == 0 && allMicGood == states.Count &&
                    knownAllIds.SetEquals(states.Keys) && allMicMutedCount == (targetMuted ? allMicGood : 0);
                if (!targetMuted && complete)
                {
                    unmuteRetryIds.Clear();
                    previousAllStates.Clear();
                    if (!MuteSessionStore.Delete()) Log("已完成开麦，闭麦记录清理失败。");
                }
                if (!targetMuted && !complete)
                    Log("统一开麦未完成。同一组设备都可读后，再次按键可重试尚未完成的开麦。");
                Log(cause + "：" + (targetMuted ? "统一静音全部" : "统一打开全部") +
                    (complete ? "已确认完成" : "尚未确认完成") +
                    "；操作中已确认 " + succeeded + "，失败 " + failed + "，无需改动 " + unchanged);
                if (complete && cfg.Sound && sound != null) sound.Play(targetMuted, cfg.WarmupMs);
                return complete;
            }
            catch (Exception ex)
            {
                LogError("全部麦克风切换失败：" + Error(ex));
                RefreshAllMics();
                return false;
            }
        }

        public void ExplicitUnmuteAll(IWin32Window owner)
        {
            if (!cfg.AllMics || audio == null) return;
            if (MessageBox.Show(owner, "即将尝试打开所有活动麦克风，包括原本被单独静音的设备。\n确定要执行吗？", "明确打开全部麦克风", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            ToggleAllMics("用户确认打开全部麦克风", true);
        }

        public void SetBridge(bool value)
        {
            cfg.Bridge = value;
            cfg.Save();
            Log(value ? "扬声器静音键切麦已开启。" : "扬声器静音键切麦已关闭。", false);
            UpdateUi();
        }
        public void SetSound(bool value)
        {
            cfg.Sound = value;
            cfg.Save();
            UpdateUi();
        }

        public int SoundVolumePercent { get { return cfg.SoundVolumePercent; } }

        // Rebuild both sets of cached WAVs before committing any setting.
        // This keeps trim/prewarm intact and avoids half-applied volume changes.
        public bool SetSoundVolumePercent(int percent)
        {
            if (percent < 0 || percent > 100) return false;
            if (percent == cfg.SoundVolumePercent) return true;
            TonePlayer replacement = null;
            try
            {
                replacement = new TonePlayer(cfg.MutePreset, cfg.UnmutePreset,
                    cfg.MuteTrimStartMs, cfg.MuteTrimEndMs, cfg.UnmuteTrimStartMs,
                    cfg.UnmuteTrimEndMs, percent);
                TonePlayer previous = sound;
                sound = replacement;
                replacement = null;
                cfg.SoundVolumePercent = percent;
                cfg.Save();
                if (previous != null) previous.Dispose();
                Log("音效音量已调整为 " + percent + "%（不影响麦克风或游戏音量）");
                UpdateUi();
                return true;
            }
            catch (Exception ex)
            {
                if (replacement != null) replacement.Dispose();
                LogError("音效音量设置失败：" + Error(ex));
                MessageBox.Show("音量设置失败，原音效仍保留：\n" + ex.Message,
                    "MicMuteBridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }

        public int WarmupMs { get { return cfg.WarmupMs; } }

        public void SetWarmupMs(int value)
        {
            if (!SoundWarmup.Contains(value)) return;
            cfg.WarmupMs = value;
            cfg.Save();
            Log("音效预热已设为 " + value + "ms（麦克风开关不会等待）");
            UpdateUi();
        }

        public string MutePreset { get { return cfg.MutePreset; } }
        public string UnmutePreset { get { return cfg.UnmutePreset; } }

        public bool ChooseSoundPreset(bool muted, string id)
        {
            if (sound == null) { Log("音效播放器不可用，无法设置音效"); return false; }
            id = SoundCatalog.SafeId(id);
            try
            {
                // A different preset starts at full duration; avoid applying a
                // previous sound's start/end offsets to an unrelated recording.
                bool changed = !String.Equals(muted ? cfg.MutePreset : cfg.UnmutePreset, id,
                    StringComparison.OrdinalIgnoreCase);
                int start = changed ? 0 : (muted ? cfg.MuteTrimStartMs : cfg.UnmuteTrimStartMs);
                int end = changed ? 0 : (muted ? cfg.MuteTrimEndMs : cfg.UnmuteTrimEndMs);
                sound.SetCue(muted, id, start, end);
                if (muted)
                {
                    cfg.MutePreset = id;
                    cfg.MuteTrimStartMs = start;
                    cfg.MuteTrimEndMs = end;
                }
                else
                {
                    cfg.UnmutePreset = id;
                    cfg.UnmuteTrimStartMs = start;
                    cfg.UnmuteTrimEndMs = end;
                }
                cfg.Save();
                Log((muted ? "闭麦" : "开麦") + "音效已切换为：" + SoundCatalog.LabelOf(id));
                UpdateUi();
                return true;
            }
            catch (Exception ex)
            {
                LogError("音效设置失败：" + Error(ex));
                MessageBox.Show("无法加载音效：\n" + ex.Message + "\n请尝试另一段 WAV 文件。", "MicMuteBridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }

        public bool ImportCustomSound(bool muted, string source)
        {
            try
            {
                // Validate before copying, so a bad file cannot overwrite a working cue.
                WavPrep.Build(source, 300, 90);
                string dest = SoundCatalog.CustomPath(muted);
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                string temp = dest + ".tmp";
                if (!String.Equals(Path.GetFullPath(source), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(source, temp, true);
                    try { File.Copy(temp, dest, true); }
                    finally { if (File.Exists(temp)) File.Delete(temp); }
                }
                // Re-import may replace a custom file with a different duration.
                // Explicitly reset old trimming; stale offsets must not survive.
                sound.SetCue(muted, "custom", 0, 0);
                if (muted)
                {
                    cfg.MutePreset = "custom";
                    cfg.MuteTrimStartMs = 0; cfg.MuteTrimEndMs = 0;
                }
                else
                {
                    cfg.UnmutePreset = "custom";
                    cfg.UnmuteTrimStartMs = 0; cfg.UnmuteTrimEndMs = 0;
                }
                cfg.Save();
                UpdateUi();
                Log("导入自定义音效并重置裁剪：" + (muted ? "闭麦" : "开麦"));
                return true;
            }
            catch (Exception ex)
            {
                LogError("导入自定义音效失败：" + Error(ex));
                MessageBox.Show("导入失败：\n" + ex.Message + "\n建议使用 16-bit PCM WAV，长度 0.1～1 秒。", "MicMuteBridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }

        public byte[] ReadSound(bool muted)
        {
            return SoundCatalog.ReadCue(muted ? cfg.MutePreset : cfg.UnmutePreset, muted);
        }
        public int TrimStart(bool muted) { return muted ? cfg.MuteTrimStartMs : cfg.UnmuteTrimStartMs; }
        public int TrimEnd(bool muted) { return muted ? cfg.MuteTrimEndMs : cfg.UnmuteTrimEndMs; }

        public bool SetSoundTrim(bool muted, int startMs, int endMs)
        {
            if (sound == null) return false;
            string preset = muted ? cfg.MutePreset : cfg.UnmutePreset;
            try
            {
                sound.SetCue(muted, preset, startMs, endMs);
                if (muted) { cfg.MuteTrimStartMs = startMs; cfg.MuteTrimEndMs = endMs; }
                else { cfg.UnmuteTrimStartMs = startMs; cfg.UnmuteTrimEndMs = endMs; }
                cfg.Save();
                Log((muted ? "闭麦" : "开麦") + "裁剪已保存：" + startMs + "～" +
                    (endMs == 0 ? "自然结尾" : endMs + "ms"));
                UpdateUi();
                return true;
            }
            catch (Exception ex)
            {
                LogError("设置裁剪失败：" + Error(ex));
                MessageBox.Show("裁剪失败：\n" + ex.Message, "MicMuteBridge",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }

        public void OpenSoundLibrary()
        {
            if (soundWindow == null || soundWindow.IsDisposed)
                soundWindow = new SoundLibraryForm(this);
            soundWindow.Show();
            soundWindow.WindowState = FormWindowState.Normal;
            soundWindow.BringToFront();
            soundWindow.Activate();
        }

        public void TestLegacySound()
        {
            PreviewSound(true, 0, "对照试听：同一段闭麦 WAV，无预热（0ms）。");
        }

        public void TestUnmuteSound()
        {
            PreviewSound(false, cfg.WarmupMs, "测试开麦音效：预热 " + cfg.WarmupMs + "ms，仅播放提示音。");
        }

        private bool TryConnectAudio()
        {
            if (audio != null) return true;
            bool wasWaiting = audioRetryWait.IsRunning;
            try
            {
                audio = new AudioSystem();
            }
            catch (Exception ex)
            {
                audioRetryWait.Restart();
                ReportOnce("连接 Windows 音频失败，将自动重试：" + Error(ex));
                if (!wasWaiting)
                    tray.ShowBalloonTip(4000, "MicMuteBridge", "正在等待 Windows 音频，程序会自动重试。", ToolTipIcon.Info);
                return false;
            }
            audioRetryWait.Reset();
            if (wasWaiting)
            {
                lastProblem = "";
                Log("Windows 音频接口已重新连接，正在刷新设备。");
            }
            return true;
        }

        public void RefreshDevices()
        {
            if (exiting || !TryConnectAudio()) return;
            try
            {
                RebindOutput();
                if (cfg.AllMics) RefreshAllMics(); else RebindMic();
            }
            catch (Exception ex) { ReportOnce("刷新音频设备失败：" + Error(ex)); }
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (exiting) return;
            if (audio == null)
            {
                if (audioRetryWait.ElapsedMilliseconds >= AudioRetryIntervalMs) RefreshDevices();
                return;
            }
            tickCount++;
            // Check default output roughly every 200 ms. Does not rely on Studio One's process name.
            if (tickCount % 5 == 0)
            {
                try { RebindOutput(); }
                catch (Exception ex) { ReportOnce("输出检查异常：" + Error(ex)); }
            }
            if (!cfg.AllMics && mic == null && tickCount % 50 == 0)
                RebindMic(); // reclaim the same stored mic ID when the device reappears
            if (cfg.AllMics && tickCount % 12 == 0) RefreshAllMics(true);
            if (!cfg.AllMics && mic != null && tickCount % 5 == 0)
            {
                try
                {
                    bool value;
                    mic.GetMute(out value);
                    if (!micMuted.HasValue || micMuted.Value != value)
                    {
                        micMuted = value;
                        UpdateUi(); // capture external mic state changes, too
                    }
                }
                catch (Exception ex)
                {
                    ReportOnce("麦克风状态读取失败：" + Error(ex));
                    FreeMic();
                    activeMicName = "麦克风暂时不可用";
                    UpdateUi();
                }
            }
            if (output == null) return;
            try
            {
                bool now;
                output.GetMute(out now);
                if (!cfg.Bridge)
                {
                    outputLastMuted = now;
                    return;
                }
                if (!outputLastMuted && now)
                {
                    // A system multimedia mute has reached the default output endpoint.
                    // Log the bridge stage separately from microphone toggle failures.
                    Log("桥接：检测到系统扬声器静音事件 [" + activeOutputName + "]。正在恢复扬声器。");
                    // The program's own restoration produces TRUE->FALSE and is ignored.
                    output.SetMute(false, IntPtr.Zero);
                    bool speakerStillMuted;
                    output.GetMute(out speakerStillMuted);
                    outputLastMuted = speakerStillMuted;
                    if (speakerStillMuted)
                    {
                        ReportOnce("扬声器未能自动恢复，麦克风保持原状态。");
                        return;
                    }
                    if (cfg.AllMics || mic != null)
                    {
                        if (ToggleMic("系统扬声器静音键")) conversions++;
                        else Log("桥接：已接收系统静音并恢复扬声器，但麦克风切换未全部成功；请检查上面的设备日志。");
                    }
                    else ReportOnce("已恢复扬声器，但未找到指定麦克风；没有改变麦克风状态。");
                }
                else outputLastMuted = now;
            }
            catch (Exception ex)
            {
                ReportOnce("音频设备监听异常：" + Error(ex));
                FreeOutput();
                activeOutputName = "输出设备异常，等待重新连接";
                UpdateUi();
            }
        }

        public bool ToggleMic(string cause)
        {
            if (cfg.AllMics) return ToggleAllMics(cause);
            if (mic == null)
            {
                Log("未操作：指定麦克风未连接或尚未选择麦克风。");
                if (window != null && window.Visible) MessageBox.Show(window, "请在设置中选择需要控制的麦克风，或者启用自动管理全部活动麦克风。", "MicMuteBridge", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            try
            {
                bool before;
                mic.GetMute(out before);
                mic.SetMute(!before, IntPtr.Zero);
                bool after;
                mic.GetMute(out after);
                micMuted = after;
                if (after == before)
                {
                    LogError("切换未生效：驱动返回的麦克风静音状态没有变化。");
                    UpdateUi();
                    return false;
                }
                Log(cause + "：" + (after ? "麦克风已静音" : "麦克风已恢复"));
                if (cfg.Sound && sound != null) sound.Play(after, cfg.WarmupMs);
                UpdateUi();
                return true;
            }
            catch (Exception ex)
            {
                FreeMic();
                activeMicName = "麦克风出现错误，等待自动重连";
                LogError("麦克风切换失败：" + Error(ex));
                UpdateUi();
                return false;
            }
        }

        public void TestSound()
        {
            PreviewSound(true, cfg.WarmupMs, "测试闭麦音效：预热 " + cfg.WarmupMs + "ms，仅播放提示音。");
        }

        private void PreviewSound(bool muted, int warmupMs, string message)
        {
            if (sound == null)
            {
                MessageBox.Show("音效播放器启动失败。请到「详细设置」打开报错文件夹，查看 RUNTIME_ERROR.log。",
                    "MicMuteBridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Log(message);
            sound.Play(muted, warmupMs);
        }

        private void UpdateUi()
        {
            bool mixed = cfg.AllMics && allMicGood > 0 && (allMicFailures > 0 || (allMicMutedCount > 0 && allMicMutedCount < allMicGood));
            tray.Icon = !micMuted.HasValue ? iconMissing : (mixed ? iconPartial : (micMuted.Value ? iconOff : iconOn));
            string statusText = audio == null ? AudioWaitingText :
                !micMuted.HasValue ? "未发现可访问麦克风" : mixed ? "部分静音/设备异常" :
                (micMuted.Value ? "麦克风已静音" : "麦克风已开启");
            tray.Text = "MicMuteBridge " + Program.Version + " - " + statusText + (cfg.Bridge ? "" : " (静音键切麦已关闭)");
            RefreshMenu();
            if (Changed != null) Changed(this, EventArgs.Empty);
        }

        private void RefreshMenu()
        {
            if (cfg.AllMics)
            {
                AllMicAction action = AllMicToggleDecision.Decide(allMicGood, allMicMutedCount, allMicFailures,
                    knownAllIds, unmuteRetryIds);
                muteMenu.Text = action == AllMicAction.Mute ? "全部闭麦" :
                    action == AllMicAction.Unmute ? "全部开麦" :
                    allMicGood == 0 ? "麦克风不可用" : "等待麦克风状态";
            }
            else
            {
                muteMenu.Text = !micMuted.HasValue ? "麦克风不可用" :
                    micMuted.Value ? "开麦" : "闭麦";
            }
            muteMenu.Enabled = cfg.AllMics ? allMicGood > 0 : mic != null;
            soundMenu.Checked = cfg.Sound;
        }

        public void OpenLogFolder(bool errors)
        {
            try
            {
                string folder = errors ? LocalStorage.Current.ErrorsFolder : Diagnostics.Folder;
                Directory.CreateDirectory(folder);
                Process.Start("explorer.exe", "\"" + folder + "\"");
            }
            catch (Exception ex) { LogError("打开日志目录失败：" + Error(ex)); }
        }

        public void OpenSettings()
        {
            if (window == null || window.IsDisposed)
                window = new SettingsForm(this);
            window.RefreshMicChoices();
            window.SyncFromContext();
            window.Show();
            window.WindowState = FormWindowState.Normal;
            window.ShowInTaskbar = true;
            window.Activate();
        }

        public void OpenShortcutSettings()
        {
            if (desktopPaused || exiting) return;
            SetDesktopPaused(true);
            try
            {
                using (var dialog = new ShortcutSettingsForm(this))
                {
                    if (window != null && window.Visible) dialog.ShowDialog(window);
                    else dialog.ShowDialog();
                }
            }
            finally { if (!exiting) SetDesktopPaused(false); }
        }

        public DesktopShortcut SavedDesktopShortcut { get { return cfg.DesktopKeys; } }
        public DesktopShortcut ActiveDesktopShortcut { get; private set; }

        public DesktopRawInput GetDesktopInput()
        {
            if (desktopInput == null)
            {
                desktopInput = new DesktopRawInput(() => ToggleFromDesktopShortcut(true), LogError);
                desktopInput.SetPaused(desktopPaused);
            }
            try { desktopInput.SetMappedMuteKeys(GameKeySetup.MappedMuteKeys()); }
            catch (Exception ex) { LogError("读取系统静音键映射失败：" + Error(ex)); }
            return desktopInput;
        }

        private void SetDesktopPaused(bool paused)
        {
            desktopPaused = paused;
            if (desktopInput != null) desktopInput.SetPaused(paused);
        }

        private void ToggleFromDesktopShortcut(bool recorded)
        {
            if (!exiting && !desktopPaused && ActiveDesktopShortcut.Enabled &&
                recorded == (ActiveDesktopShortcut.Chord != null))
                ToggleMic("常规快捷键");
        }

        public bool SaveDesktopShortcut(DesktopShortcut shortcut, out string problem)
        {
            DesktopShortcut previousActive = ActiveDesktopShortcut;
            if (!TryApplyDesktopShortcut(shortcut, out problem))
            {
                LogError("常规快捷键设置失败：" + problem);
                return false;
            }
            DesktopShortcut previousSaved = cfg.DesktopKeys;
            cfg.DesktopKeys = shortcut;
            try { cfg.SaveTo(Settings.PathName, Diagnostics.Write); }
            catch (Exception ex)
            {
                cfg.DesktopKeys = previousSaved;
                problem = "保存常规快捷键失败：" + Error(ex);
                string restoreProblem;
                if (!TryApplyDesktopShortcut(previousActive, out restoreProblem))
                    problem += Environment.NewLine + "恢复先前的快捷键失败：" + restoreProblem;
                LogError(problem);
                return false;
            }
            Log(shortcut.Enabled ? "常规快捷键已启用。" : "常规快捷键已关闭。");
            return true;
        }

        private bool TryApplyDesktopShortcut(DesktopShortcut shortcut, out string problem)
        {
            problem = "";
            DesktopShortcut before = ActiveDesktopShortcut;
            try
            {
                if (shortcut.Chord != null)
                {
                    DesktopRawInput input = GetDesktopInput();
                    if (input.UsesMappedMuteKey(shortcut.Chord))
                    {
                        problem = "这组键包含已映射为扬声器静音的键，请重新录制。";
                        return false;
                    }
                    input.SetChord(shortcut.Chord);
                    if (desktopHotKey != null && !desktopHotKey.TrySet(default(DesktopShortcut), out problem))
                    {
                        input.SetChord(before.Chord);
                        return false;
                    }
                }
                else
                {
                    if (shortcut.Enabled && desktopHotKey == null)
                        desktopHotKey = new DesktopHotKey(() => ToggleFromDesktopShortcut(false));
                    if (desktopHotKey != null && !desktopHotKey.TrySet(shortcut, out problem)) return false;
                    if (desktopInput != null)
                    {
                        try { desktopInput.SetChord(null); }
                        catch
                        {
                            string ignored;
                            if (desktopHotKey != null) desktopHotKey.TrySet(before, out ignored);
                            throw;
                        }
                    }
                }
                ActiveDesktopShortcut = shortcut;
                return true;
            }
            catch (Exception ex)
            {
                problem = Error(ex);
                return false;
            }
        }

        private static string StartupKeyPath { get { return @"Software\Microsoft\Windows\CurrentVersion\Run"; } }
        private bool IsStartupOn()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(StartupKeyPath, false))
                {
                    if (key == null) return false;
                    string value = key.GetValue("MicMuteBridge") as string;
                    return String.Equals(value, "\"" + Application.ExecutablePath + "\"", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { return false; }
        }

        public void SetStartup(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(StartupKeyPath))
                {
                    if (enabled) key.SetValue("MicMuteBridge", "\"" + Application.ExecutablePath + "\"");
                    else key.DeleteValue("MicMuteBridge", false);
                }
                Log("开机自启" + (enabled ? "已开启" : "已关闭"));
                UpdateUi();
            }
            catch (Exception ex) { LogError("更改开机自启失败：" + Error(ex)); MessageBox.Show("更改开机自启失败：" + ex.Message, "MicMuteBridge", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        public void Log(string message) { Log(message, true); }
        private void LogError(string message) { Log(message, true, true); }
        private void Log(string message, bool notify, bool error = false)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + message;
            if (error) Diagnostics.WriteError(message);
            else Diagnostics.Write(message);
            logLines.Add(line);
            if (logLines.Count > 120) logLines.RemoveAt(0);
            if (notify && window != null && !window.IsDisposed && window.Visible) window.SyncLogs();
        }
        private void ReportOnce(string message)
        {
            if (message == lastProblem) return;
            lastProblem = message;
            LogError(message);
            UpdateUi();
        }

        public void ExitBridge()
        {
            exiting = true;
            timer.Stop();
            if (desktopHotKey != null) desktopHotKey.Dispose();
            if (desktopInput != null) desktopInput.Dispose();
            tray.Visible = false;
            if (window != null && !window.IsDisposed) { window.AllowClose = true; window.Close(); }
            if (soundWindow != null && !soundWindow.IsDisposed) soundWindow.Close();
            FreeOutput();
            FreeMic();
            if (sound != null) sound.Dispose();
            tray.Dispose();
            menu.Dispose();
            iconOn.Dispose(); iconOff.Dispose(); iconMissing.Dispose(); iconPartial.Dispose();
            cfg.Save();
            ExitThread();
        }
    }

    internal sealed class GameKeyCaptureDialog : Form, IMessageFilter
    {
        private const int WmKeyDown = 0x0100;
        private const int WmKeyUp = 0x0101;
        private const int WmSysKeyDown = 0x0104;
        private const int WmSysKeyUp = 0x0105;
        private const int WasDown = 1 << 30;
        private const string Instructions = "按下一个键，松开后选中。";
        private readonly HashSet<ushort> held = new HashSet<ushort>();
        private readonly Label status = new Label { Text = Instructions };
        private GameKeyOption pending;
        private GameKeyOption selected;

        public GameKeyCaptureDialog()
        {
            Text = "录制游戏单键";
            ClientSize = new Size(436, 126);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            MaximizeBox = false;
            Font = new Font("Microsoft YaHei UI", 9.5F); try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            status.SetBounds(16, 16, 404, 28);
            var help = new Label { Text = "点击「取消」可退出。已映射的键可从列表选回。" };
            help.SetBounds(16, 49, 404, 27);
            var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel };
            cancel.SetBounds(330, 81, 90, 30);
            Controls.AddRange(new Control[] { status, help, cancel });
            CancelButton = cancel;
        }

        public GameKeyOption Record(IWin32Window owner)
        {
            Application.AddMessageFilter(this);
            try { return ShowDialog(owner) == DialogResult.OK ? selected : null; }
            finally { Application.RemoveMessageFilter(this); }
        }

        protected override void OnDeactivate(EventArgs e)
        {
            held.Clear();
            pending = null;
            status.Text = Instructions;
            base.OnDeactivate(e);
        }

        bool IMessageFilter.PreFilterMessage(ref Message message)
        {
            bool pressed = message.Msg == WmKeyDown || message.Msg == WmSysKeyDown;
            if ((!pressed && message.Msg != WmKeyUp && message.Msg != WmSysKeyUp) || !ContainsFocus)
                return false;

            int keyData = unchecked((int)message.LParam.ToInt64());
            Keys key = (Keys)message.WParam.ToInt32();
            ushort scanCode = GameKeyMap.RecordableScanCode(key, keyData);
            if (pressed)
            {
                if ((keyData & WasDown) != 0) return true;
                if (scanCode == 0)
                {
                    pending = null;
                    status.Text = "这个键无法用作系统单键，请试另一个键。";
                    return true;
                }
                held.Add(scanCode);
                if (held.Count != 1)
                {
                    pending = null;
                    status.Text = "请松开全部按键，再只按一个键。";
                    return true;
                }
                pending = GameKeyMap.FromScanCode(scanCode);
                status.Text = "已按下 " + pending + "，松开后选中。";
            }
            else if (held.Remove(scanCode))
            {
                if (held.Count == 0 && pending != null) AcceptKey(pending);
            }
            else if (held.Count == 0 && ModifierKeys == Keys.None && GameKeyMap.CanCaptureOnRelease(scanCode))
            {
                AcceptKey(GameKeyMap.FromScanCode(scanCode));
            }
            return true;
        }

        private void AcceptKey(GameKeyOption choice)
        {
            selected = choice;
            DialogResult = DialogResult.OK;
        }
    }

    internal sealed class DesktopShortcutDialog : Form, IMessageFilter
    {
        private readonly DesktopRawInput input;
        private readonly Label status = new Label { Text = DesktopChordRecording.Instructions };
        private DesktopChord result;
        private bool recording;

        public DesktopShortcutDialog(DesktopRawInput listener)
        {
            input = listener;
            Text = "录制常规快捷键";
            ClientSize = new Size(512, 147);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            MaximizeBox = false;
            Font = new Font("Microsoft YaHei UI", 9.5F); try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            status.SetBounds(16, 16, 480, 52);
            var help = new Label { Text = "也可以只按一个键。点击「取消」退出。" };
            help.SetBounds(16, 72, 480, 25);
            var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel };
            cancel.SetBounds(406, 102, 90, 31);
            Controls.AddRange(new Control[] { status, help, cancel });
        }

        public DesktopChord Record(IWin32Window owner)
        {
            Application.AddMessageFilter(this);
            try { return ShowDialog(owner) == DialogResult.OK ? result : null; }
            finally
            {
                Application.RemoveMessageFilter(this);
                if (recording) input.StopRecording();
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                input.StartRecording(() => ContainsFocus, OnRecorded);
                recording = true;
            }
            catch (Exception ex)
            {
                Diagnostics.WriteError("启动常规快捷键录制失败：" + ex.ToString());
                MessageBox.Show(this, "Windows 无法开始录制。\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.Cancel;
            }
        }

        protected override void OnDeactivate(EventArgs e)
        {
            input.DiscardRecording();
            status.Text = DesktopChordRecording.Instructions;
            base.OnDeactivate(e);
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            input.DiscardRecording();
        }

        private void OnRecorded(DesktopChord chord, string message)
        {
            if (result != null || !ContainsFocus) return;
            status.Text = message;
            if (chord == null) return;
            result = chord;
            DialogResult = DialogResult.OK;
        }

        bool IMessageFilter.PreFilterMessage(ref Message message)
        {
            // Keep keys such as Space, Enter and Esc from activating the dialog's buttons.
            return ContainsFocus && ((message.Msg >= 0x0100 && message.Msg <= 0x0107) ||
                message.Msg == 0x020B || message.Msg == 0x020C || message.Msg == 0x020D);
        }
    }

    internal sealed class ShortcutSettingsForm : Form
    {
        private readonly BridgeContext core;
        private readonly ComboBox gameKeys = new ComboBox();
        private readonly Label gameStatus = new Label();
        private readonly Label desktopStatus = new Label();
        private readonly TextBox desktopSelection = new TextBox { ReadOnly = true };
        private readonly Button saveDesktop;
        private DesktopShortcut pendingDesktop;

        public ShortcutSettingsForm(BridgeContext context)
        {
            core = context;
            Text = "MicMuteBridge · 麦克风快捷键设置";
            ClientSize = new Size(640, 429);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9.5F); try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            GroupBox game = AddGroup("游戏单键 · 系统静音触发", 14, 162);
            AddLabel(game, "检测到 Windows 扬声器静音后，自动恢复扬声器并切换麦克风。", 25, 23);
            AddLabel(game, "此键在系统中改为扬声器静音，需管理员授权并重启。该方式已在国服 LOL 全屏验证。", 49, 25, true);
            gameKeys.DropDownStyle = ComboBoxStyle.DropDownList;
            gameKeys.MaxDropDownItems = 16;
            gameKeys.Items.AddRange(GameKeyMap.Choices);
            gameKeys.SelectedIndex = 0;
            gameKeys.SetBounds(16, 88, 163, 30);
            game.Controls.Add(gameKeys);
            AddButton(game, "录制单键…", 189, 86, 112, (s,e) => RecordGameKey());
            AddButton(game, "应用系统单键…", 311, 86, 136, (s,e) =>
                GameKeySetup.Launch((GameKeyOption)gameKeys.SelectedItem, this));
            AddButton(game, "撤回系统单键…", 457, 86, 135, (s,e) => GameKeySetup.Launch(null, this));
            gameStatus.SetBounds(16, 128, 576, 24);
            game.Controls.Add(gameStatus);

            GroupBox desktop = AddGroup("常规开关麦克风", 185, 184);
            AddLabel(desktop, "可录制单键、同时按住的多个键或鼠标侧键。保存并关闭窗口后可用。", 25, 23);
            AddLabel(desktop, "适合桌面和一般应用，无需管理员授权或重启。LOL 全屏请用上方游戏单键。", 49, 22, true);
            desktopSelection.SetBounds(16, 78, 576, 30);
            desktopSelection.AccessibleName = "待保存的常规快捷键";
            desktop.Controls.Add(desktopSelection);
            AddButton(desktop, "录制快捷键…", 16, 115, 116, (s,e) => RecordDesktop());
            saveDesktop = AddButton(desktop, "保存快捷键", 142, 115, 101, (s,e) => ApplyDesktop(pendingDesktop));
            AddButton(desktop, "关闭快捷键", 253, 115, 101, (s,e) => ApplyDesktop(default(DesktopShortcut)));
            desktopStatus.SetBounds(16, 153, 576, 24);
            desktop.Controls.Add(desktopStatus);

            SetDesktopSelection(core.SavedDesktopShortcut);
            RefreshDesktopStatus();
            RefreshGameStatus(true);
            Activated += (s,e) => RefreshGameStatus(false);
            CancelButton = AddButton(this, "完成", 530, 382, 94, (s,e) => Close());
        }

        private GroupBox AddGroup(string title, int top, int height)
        {
            var box = new GroupBox { Text = title };
            box.SetBounds(16, top, 608, height);
            Controls.Add(box);
            return box;
        }

        private static void AddLabel(Control parent, string text, int top, int height, bool small = false)
        {
            var label = new Label { Text = text };
            if (small)
            {
                label.Font = new Font("Microsoft YaHei UI", 8.5F);
                label.ForeColor = SystemColors.GrayText;
            }
            label.SetBounds(16, top, 576, height);
            parent.Controls.Add(label);
        }

        private static Button AddButton(Control parent, string text, int x, int y, int width, EventHandler click)
        {
            var button = new Button { Text = text };
            button.SetBounds(x, y, width, 31);
            button.Click += click;
            parent.Controls.Add(button);
            return button;
        }

        private void RecordGameKey()
        {
            using (var dialog = new GameKeyCaptureDialog())
            {
                GameKeyOption choice = dialog.Record(this);
                if (choice != null) SelectGameKey(choice);
            }
        }

        private void SelectGameKey(GameKeyOption choice)
        {
            GameKeyOption existing = gameKeys.Items.Cast<GameKeyOption>().FirstOrDefault(item => item.ScanCode == choice.ScanCode);
            if (existing == null) gameKeys.Items.Add(choice);
            gameKeys.SelectedItem = existing ?? choice;
        }

        private void RefreshGameStatus(bool selectSaved)
        {
            try
            {
                GameKeyOption[] saved = GameKeySetup.SavedKeys();
                gameStatus.Text = saved.Length == 0 ? "本工具写入的系统单键：无" :
                    "已保存到 Windows：" + String.Join("、", saved.Select(key => key.ToString()).ToArray());
                if (selectSaved && saved.Length > 0) SelectGameKey(saved[0]);
            }
            catch (Exception ex)
            {
                gameStatus.Text = "读取系统单键失败，请查看日志。";
                Diagnostics.WriteError("读取系统单键失败：" + ex.ToString(), "KEY_MAPPING_ERROR");
            }
        }

        private void RefreshDesktopStatus()
        {
            DesktopShortcut active = core.ActiveDesktopShortcut;
            desktopStatus.Text = active.Enabled ? "已启用：" + active :
                core.SavedDesktopShortcut.Enabled ? "已保存的快捷键未能启用，可重新录制。" : "常规快捷键已关闭。";
        }

        private void SetDesktopSelection(DesktopShortcut shortcut)
        {
            pendingDesktop = shortcut;
            desktopSelection.Text = shortcut.Enabled ? shortcut.ToString() : "尚未录制";
            saveDesktop.Enabled = shortcut.Enabled;
        }

        private void RecordDesktop()
        {
            try
            {
                using (var dialog = new DesktopShortcutDialog(core.GetDesktopInput()))
                {
                    DesktopChord chord = dialog.Record(this);
                    if (chord != null) SetDesktopSelection(new DesktopShortcut(chord));
                }
            }
            catch (Exception ex)
            {
                Diagnostics.WriteError("常规快捷键录制失败：" + ex.ToString());
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void ApplyDesktop(DesktopShortcut shortcut)
        {
            string problem;
            if (!core.SaveDesktopShortcut(shortcut, out problem))
                MessageBox.Show(this, problem, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else SetDesktopSelection(shortcut);
            RefreshDesktopStatus();
        }
    }

    internal sealed class SettingsForm : Form
    {
        private readonly BridgeContext core;
        private readonly ComboBox micChoice = new ComboBox();
        private readonly Label outputLabel = new Label();
        private readonly Label micLabel = new Label();
        private readonly Label stateLabel = new Label();
        private readonly Label counts = new Label();
        private readonly CheckBox bridgeBox = new CheckBox();
        private readonly CheckBox allMicsBox = new CheckBox();
        private readonly Button unmuteAllButton;
        private readonly CheckBox soundBox = new CheckBox();
        private readonly ComboBox soundModeChoice = new ComboBox();
        private readonly CheckBox startupBox = new CheckBox();
        private readonly TextBox logText = new TextBox();
        private readonly TextBox micDetailText = new TextBox();
        private bool syncing;
        public bool AllowClose;

        public SettingsForm(BridgeContext context)
        {
            core = context;
            Text = "MicMuteBridge " + Program.Version + " · 详细设置";
            ClientSize = new Size(720, 678);
            MinimumSize = new Size(735, 710);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9.5F); try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            BackColor = Color.FromArgb(248, 249, 252);

            AddLabel("MicMuteBridge", 22, 16, 480, 38, 19F, true);
            AddButton("快捷键设置…", 546, 20, 145, 32, (s,e) => core.OpenShortcutSettings());
            AddLabel("左键点托盘图标可切麦。游戏单键和常规切麦键可在右上角设置。", 22, 60, 655, 29, 9.5F);

            stateLabel.SetBounds(22, 110, 665, 32);
            stateLabel.Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold);
            Controls.Add(stateLabel);
            outputLabel.SetBounds(22, 155, 665, 29);
            Controls.Add(outputLabel);
            micLabel.SetBounds(22, 190, 665, 29);
            Controls.Add(micLabel);

            AddLabel("麦克风控制目标（默认自动识别；可手动指定）", 22, 227, 660, 25, 10F, true);
            micChoice.SetBounds(22, 255, 545, 31);
            micChoice.DropDownStyle = ComboBoxStyle.DropDownList;
            Controls.Add(micChoice);
            micChoice.SelectedIndexChanged += (s,e) =>
            {
                if (syncing) return;
                AudioDevice chosen = micChoice.SelectedItem as AudioDevice;
                if (chosen != null) core.SelectMic(chosen.Id);
            };
            AddButton("刷新", 579, 255, 112, 31, (s,e) => { core.RefreshDevices(); FillMicChoices(); });
            allMicsBox.SetBounds(22, 293, 463, 31);
            allMicsBox.Text = "自动控制全部活动麦克风（下次按键统一开麦 / 闭麦）";
            allMicsBox.CheckedChanged += (s,e) => { if (!syncing) { core.SetAllMics(allMicsBox.Checked); SyncFromContext(); } };
            Controls.Add(allMicsBox);
            unmuteAllButton = AddButton("确认打开全部麦克风…", 491, 293, 200, 31, (s,e) => core.ExplicitUnmuteAll(this));

            bridgeBox.SetBounds(22, 329, 350, 28);
            bridgeBox.Text = "用扬声器静音键切换麦克风";
            bridgeBox.CheckedChanged += (s,e) => { if (!syncing) core.SetBridge(bridgeBox.Checked); };
            Controls.Add(bridgeBox);
            AddLabel("开启时按键切麦并恢复扬声器，关闭时只控制扬声器。", 40, 359, 335, 21, 8.5F);
            soundBox.SetBounds(390, 329, 300, 28);
            soundBox.Text = "播放开麦 / 闭麦提示音";
            soundBox.CheckedChanged += (s,e) => { if (!syncing) core.SetSound(soundBox.Checked); };
            Controls.Add(soundBox);
            startupBox.SetBounds(22, 383, 340, 29);
            startupBox.Text = "登录 Windows 后自动启动（托盘常驻）";
            startupBox.CheckedChanged += (s,e) => { if (!syncing) core.SetStartup(startupBox.Checked); };
            Controls.Add(startupBox);
            AddLabel("预热时间", 390, 389, 74, 24, 9F);
            soundModeChoice.SetBounds(465, 382, 226, 31);
            soundModeChoice.DropDownStyle = ComboBoxStyle.DropDownList;
            foreach (int ms in SoundWarmup.Milliseconds)
                soundModeChoice.Items.Add(ms == 300 ? "300ms（已验证）" :
                    ms == 0 ? "0ms（实验 / 可能吞音）" : ms + "ms（实验）");
            soundModeChoice.SelectedIndexChanged += (s,e) =>
            {
                if (!syncing && soundModeChoice.SelectedIndex >= 0)
                    core.SetWarmupMs(SoundWarmup.Milliseconds[soundModeChoice.SelectedIndex]);
            };
            Controls.Add(soundModeChoice);

            AddButton("立即切换麦克风", 22, 435, 156, 38, (s,e) => core.ToggleMic("主窗口"));
            AddButton("试听闭麦", 186, 435, 112, 38, (s,e) => core.TestSound());
            AddButton("音效库 / 音量…", 305, 435, 140, 38, (s,e) => core.OpenSoundLibrary());
            AddButton("隐藏到托盘", 565, 435, 126, 38, (s,e) => Hide());
            counts.SetBounds(459, 441, 105, 26);
            Controls.Add(counts);

            TabControl detailTabs = new TabControl();
            detailTabs.SetBounds(22, 492, 669, 134);
            TabPage devicesTab = new TabPage("麦克风明细");
            TabPage logsTab = new TabPage("运行日志");
            micDetailText.Dock = DockStyle.Fill;
            micDetailText.Multiline = true;
            micDetailText.ScrollBars = ScrollBars.Vertical;
            micDetailText.ReadOnly = true;
            micDetailText.WordWrap = false;
            micDetailText.BackColor = Color.White;
            micDetailText.Font = new Font("Microsoft YaHei UI", 9F);
            devicesTab.Controls.Add(micDetailText);
            logText.Dock = DockStyle.Fill;
            logText.Multiline = true;
            logText.ScrollBars = ScrollBars.Vertical;
            logText.ReadOnly = true;
            logText.BackColor = Color.White;
            logText.Font = new Font("Consolas", 9F);
            logsTab.Controls.Add(logText);
            detailTabs.TabPages.Add(devicesTab);
            detailTabs.TabPages.Add(logsTab);
            Controls.Add(detailTabs);
            AddButton("打开日志文件夹", 22, 630, 132, 32, (s,e) => core.OpenLogFolder(false));
            AddButton("打开报错文件夹", 162, 630, 132, 32, (s,e) => core.OpenLogFolder(true));
            AddLabel("关闭窗口会隐藏到托盘。退出请用托盘右键菜单。", 310, 633, 381, 24, 8F);

            core.Changed += CoreChanged;
            FormClosing += (s,e) => { if (!AllowClose) { e.Cancel = true; Hide(); } };
            FormClosed += (s,e) => core.Changed -= CoreChanged;
            FillMicChoices();
            SyncFromContext();
        }

        private void CoreChanged(object sender, EventArgs e)
        {
            if (!IsDisposed && Visible) SyncFromContext();
        }

        private Label AddLabel(string text, int x, int y, int w, int h, float size, bool bold = false)
        {
            Label label = new Label();
            label.Text = text;
            label.SetBounds(x, y, w, h);
            label.Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular);
            Controls.Add(label);
            return label;
        }
        private Button AddButton(string text, int x, int y, int w, int h, EventHandler click)
        {
            Button button = new Button();
            button.Text = text;
            button.SetBounds(x, y, w, h);
            button.Click += click;
            Controls.Add(button);
            return button;
        }

        private void FillMicChoices()
        {
            syncing = true;
            micChoice.Items.Clear();
            List<AudioDevice> microphones = core.GetCaptureDevices();
            int index = -1;
            for (int i = 0; i < microphones.Count; i++)
            {
                micChoice.Items.Add(microphones[i]);
                if (microphones[i].Id == core.SavedMicId) index = i;
            }
            // Never select a different mic silently. A manual choice is required.
            micChoice.SelectedIndex = index;
            syncing = false;
        }

        public void RefreshMicChoices()
        {
            FillMicChoices();
        }

        public void SyncLogs()
        {
            logText.Text = core.Logs;
            logText.SelectionStart = logText.Text.Length;
            logText.ScrollToCaret();
        }

        public void SyncFromContext()
        {
            if (IsDisposed) return;
            syncing = true;
            string detailValue = core.MicDetails;
            if (micDetailText.Text != detailValue) micDetailText.Text = detailValue;
            stateLabel.Text = core.AllMicsOn ?
                "●  " + core.AllMicStatus :
                !core.MicMuted.HasValue ? "○  麦克风未连接 / 未选择" :
                core.MicMuted.Value ? "●  麦克风已静音" : "●  麦克风开启";
            stateLabel.ForeColor = !core.MicMuted.HasValue ? Color.Gray :
                (core.AllMicsOn && (core.AllMicStatus.StartsWith("部分") || core.AllMicStatus.Contains("出错"))) ? Color.FromArgb(190, 124, 20) :
                core.MicMuted.Value ? Color.FromArgb(205, 65, 69) : Color.FromArgb(25, 145, 82);
            outputLabel.Text = "自动监听的输出设备：" + core.OutputName;
            micLabel.Text = "控制的麦克风设备：" + core.MicName;
            allMicsBox.Checked = core.AllMicsOn;
            micChoice.Enabled = !core.AllMicsOn;
            unmuteAllButton.Enabled = core.CanRequestAllMicUnmute;
            bridgeBox.Checked = core.BridgeOn;
            soundBox.Checked = core.SoundOn;
            soundModeChoice.SelectedIndex = SoundWarmup.IndexOf(core.WarmupMs);
            startupBox.Checked = core.StartupOn;
            counts.Text = "已转换 " + core.Conversions + " 次";
            SyncLogs();
            syncing = false;
        }
    }

    // Sound selection is deliberately isolated from the already-tested mic bridge.
    internal sealed class SoundLibraryForm : Form
    {
        private readonly BridgeContext core;
        private readonly ComboBox muteChoice = new ComboBox();
        private readonly ComboBox unmuteChoice = new ComboBox();
        private readonly TrackBar volumeSlider = new TrackBar();
        private readonly Label volumeValue = new Label();
        private bool syncing;

        public SoundLibraryForm(BridgeContext context)
        {
            core = context;
            Text = "MicMuteBridge · 音效库 / 音量调节";
            ClientSize = new Size(700, 451);
            MinimumSize = new Size(710, 490);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Microsoft YaHei UI", 9.5F); try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            BackColor = Color.FromArgb(248, 249, 252);
            Label heading = new Label { Text = "选择提示音", Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Bold) };
            heading.SetBounds(20, 14, 540, 38); Controls.Add(heading);
            Label tip = new Label { Text = "分别选择和裁剪开麦 / 闭麦声音；音量只影响提示音，不影响游戏及麦克风。" };
            tip.SetBounds(20, 55, 660, 24); Controls.Add(tip);

            MakeRow("闭麦", muteChoice, true, 100);
            MakeRow("开麦", unmuteChoice, false, 160);

            Label volumeTitle = new Label { Text = "提示音音量", Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold) };
            volumeTitle.SetBounds(20, 236, 105, 30); Controls.Add(volumeTitle);
            volumeSlider.SetBounds(130, 223, 338, 52);
            volumeSlider.Minimum = 0; volumeSlider.Maximum = 100;
            volumeSlider.SmallChange = 5; volumeSlider.LargeChange = 10;
            volumeSlider.TickFrequency = 10;
            volumeSlider.Value = core.SoundVolumePercent;
            volumeValue.SetBounds(470, 232, 60, 30);
            volumeValue.Text = volumeSlider.Value + "%";
            volumeSlider.ValueChanged += (s,e) => volumeValue.Text = volumeSlider.Value + "%";
            Controls.Add(volumeSlider); Controls.Add(volumeValue);
            AddButton("应用音量", 546, 225, 129, 34, (s,e) =>
            {
                if (!core.SetSoundVolumePercent(volumeSlider.Value))
                    volumeSlider.Value = core.SoundVolumePercent;
            });

            Label note = new Label {
                Text = "WAV 需为未压缩 PCM / 32-bit Float，最长 3 秒。裁剪不会修改原 WAV 文件；\n" +
                       "音效会复制到本机用户配置目录，换电脑时需重新导入自定义声音。",
                ForeColor = Color.FromArgb(95, 103, 112)
            };
            note.SetBounds(20, 297, 650, 48); Controls.Add(note);
            AddButton("恢复两种默认音效", 20, 390, 190, 34, (s,e) =>
            {
                core.ChooseSoundPreset(true, "classic");
                core.ChooseSoundPreset(false, "classic");
                core.SetSoundTrim(true, 0, 0);
                core.SetSoundTrim(false, 0, 0);
                SyncSelections();
            });
            AddButton("试听闭麦（无预热）", 223, 390, 171, 34, (s,e) => core.TestLegacySound());
            AddButton("完成", 575, 390, 99, 34, (s,e) => Close());
            SyncSelections();
        }

        private void MakeRow(string label, ComboBox choice, bool muted, int y)
        {
            Label title = new Label { Text = label + "提示音", Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold) };
            title.SetBounds(20, y + 8, 95, 27); Controls.Add(title);
            choice.SetBounds(110, y + 2, 239, 31);
            choice.DropDownStyle = ComboBoxStyle.DropDownList;
            for (int i = 0; i < SoundCatalog.Ids.Length; i++)
                choice.Items.Add(new PresetItem(SoundCatalog.Ids[i], SoundCatalog.Names[i]));
            choice.SelectedIndexChanged += (s,e) =>
            {
                if (syncing) return;
                PresetItem chosen = choice.SelectedItem as PresetItem;
                if (chosen != null)
                {
                    core.ChooseSoundPreset(muted, chosen.Id);
                    SyncSelections();
                }
            };
            Controls.Add(choice);
            AddButton("试听", 356, y + 1, 63, 32, (s,e) =>
            {
                if (muted) core.TestSound(); else core.TestUnmuteSound();
            });
            AddButton("裁剪…", 427, y + 1, 80, 32, (s,e) =>
            {
                using (var editor = new SoundTrimForm(core, muted)) editor.ShowDialog(this);
            });
            AddButton("导入 WAV…", 514, y + 1, 162, 32, (s,e) =>
            {
                using (var picker = new OpenFileDialog())
                {
                    picker.Title = "选择" + label + " WAV 音效";
                    picker.Filter = "WAV 音频 (*.wav)|*.wav";
                    picker.CheckFileExists = true;
                    if (picker.ShowDialog(this) == DialogResult.OK)
                    {
                        core.ImportCustomSound(muted, picker.FileName);
                        SyncSelections();
                    }
                }
            });
        }

        private Button AddButton(string title, int x, int y, int w, int h, EventHandler onClick)
        {
            Button b = new Button { Text = title };
            b.SetBounds(x, y, w, h); b.Click += onClick; Controls.Add(b); return b;
        }
        private void SyncSelections()
        {
            syncing = true;
            SelectOption(muteChoice, core.MutePreset);
            SelectOption(unmuteChoice, core.UnmutePreset);
            syncing = false;
        }
        private static void SelectOption(ComboBox choice, string id)
        {
            for (int i = 0; i < choice.Items.Count; i++)
            {
                PresetItem item = choice.Items[i] as PresetItem;
                if (item != null && item.Id == id) { choice.SelectedIndex = i; return; }
            }
            choice.SelectedIndex = 0;
        }
    }

    // Non-destructive WAV clip editor. Start/end are in source milliseconds,
    // independent of prewarm and never delay the microphone operation.
    internal sealed class SoundTrimForm : Form
    {
        private readonly BridgeContext core;
        private readonly bool muted;
        private readonly NumericUpDown start = new NumericUpDown();
        private readonly NumericUpDown end = new NumericUpDown();
        private readonly Label selection = new Label();
        private readonly Panel waveform = new Panel();
        private float[] peaks;
        private int duration;

        public SoundTrimForm(BridgeContext bridge, bool isMute)
        {
            core = bridge; muted = isMute;
            Text = "裁剪" + (muted ? "闭麦" : "开麦") + "提示音";
            ClientSize = new Size(600, 330);
            MinimumSize = new Size(610, 365);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Microsoft YaHei UI", 9.5F); try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            try
            {
                byte[] source = core.ReadSound(muted);
                duration = WavPrep.DurationMs(source);
                peaks = WavPrep.Peaks(source, 120);
            }
            catch (Exception ex)
            {
                MessageBox.Show("无法读取音效：" + ex.Message, "MicMuteBridge",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                Shown += (s,e) => Close();
                return;
            }
            Label heading = new Label { Text = "选择音效片段（原始 " + duration + "ms）",
                Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold) };
            heading.SetBounds(20, 15, 560, 32); Controls.Add(heading);
            Label tip = new Label { Text = "蓝色为保留片段，灰色为舍弃片段。可精确调整起点与终点。" };
            tip.SetBounds(20, 53, 560, 24); Controls.Add(tip);

            waveform.SetBounds(20, 84, 560, 86);
            waveform.BackColor = Color.FromArgb(246, 248, 252);
            waveform.Paint += DrawWaveform;
            Controls.Add(waveform);

            Label labelStart = new Label { Text = "从 (ms)" };
            labelStart.SetBounds(20, 190, 85, 27); Controls.Add(labelStart);
            start.SetBounds(112, 188, 142, 30);
            start.Minimum = 0; start.Maximum = duration;
            start.Increment = 10;
            Controls.Add(start);
            Label labelEnd = new Label { Text = "到 (ms)" };
            labelEnd.SetBounds(300, 190, 80, 27); Controls.Add(labelEnd);
            end.SetBounds(386, 188, 142, 30);
            end.Minimum = 0; end.Maximum = duration;
            end.Increment = 10;
            Controls.Add(end);
            int currentStart = Math.Min(core.TrimStart(muted), Math.Max(0, duration - 20));
            int currentEnd = core.TrimEnd(muted) == 0 ? duration : Math.Min(duration, core.TrimEnd(muted));
            if (currentEnd - currentStart < 20) { currentStart = 0; currentEnd = duration; }
            start.Value = currentStart;
            end.Value = currentEnd;
            selection.SetBounds(20, 230, 560, 24);
            Controls.Add(selection);
            start.ValueChanged += (s,e) => UpdatePreview();
            end.ValueChanged += (s,e) => UpdatePreview();
            UpdatePreview();

            AddButton("试听当前片段", 20, 280, 151, (s,e) =>
            {
                if (TrySave())
                {
                    if (muted) core.TestSound(); else core.TestUnmuteSound();
                }
            });
            AddButton("恢复完整声音", 184, 280, 151, (s,e) =>
            {
                start.Value = 0; end.Value = duration;
                TrySave();
            });
            AddButton("保存并关闭", 438, 280, 142, (s,e) =>
            {
                if (TrySave()) Close();
            });
        }
        private Button AddButton(string name, int x, int y, int w, EventHandler onClick)
        {
            Button b = new Button { Text = name };
            b.SetBounds(x, y, w, 33);
            b.Click += onClick;
            Controls.Add(b);
            return b;
        }
        private void UpdatePreview()
        {
            int a = (int)start.Value, b = (int)end.Value;
            selection.Text = b > a ? "片段长度：" + (b - a) + "ms" : "结束位置需要晚于开始位置";
            selection.ForeColor = b - a >= 20 ? Color.FromArgb(20, 105, 70) : Color.DarkRed;
            waveform.Invalidate();
        }
        private bool TrySave()
        {
            int a = (int)start.Value, b = (int)end.Value;
            if (b - a < 20)
            {
                MessageBox.Show("保留片段至少需要 20ms，且结束时间应大于开始时间。", "MicMuteBridge",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            return core.SetSoundTrim(muted, a, b == duration ? 0 : b);
        }
        private void DrawWaveform(object sender, PaintEventArgs e)
        {
            if (peaks == null || duration <= 0) return;
            int w = waveform.ClientSize.Width, h = waveform.ClientSize.Height;
            int a = (int)start.Value, b = (int)end.Value;
            int x1 = Math.Max(0, Math.Min(w, (int)(a * (double)w / duration)));
            int x2 = Math.Max(0, Math.Min(w, (int)(b * (double)w / duration)));
            e.Graphics.Clear(Color.FromArgb(246, 248, 252));
            using (Brush selected = new SolidBrush(Color.FromArgb(219, 240, 255)))
                e.Graphics.FillRectangle(selected, x1, 0, Math.Max(0, x2 - x1), h);
            using (Pen light = new Pen(Color.FromArgb(210, 218, 229)))
                e.Graphics.DrawLine(light, 0, h / 2, w, h / 2);
            for (int i = 0; i < peaks.Length; i++)
            {
                int x = (int)((i + 0.5) * w / peaks.Length);
                int amp = Math.Max(1, (int)(peaks[i] * (h / 2 - 8)));
                using (Pen pen = new Pen(x >= x1 && x <= x2 ? Color.SteelBlue : Color.Silver, 2))
                    e.Graphics.DrawLine(pen, x, h / 2 - amp, x, h / 2 + amp);
            }
            using (Pen p = new Pen(Color.DodgerBlue, 2))
            {
                e.Graphics.DrawLine(p, x1, 0, x1, h);
                e.Graphics.DrawLine(p, x2, 0, x2, h);
            }
        }
    }

    internal static class Program
    {
        internal const string Version = "v1.2.17 Beta";
        private static Mutex mutex;
        [STAThread]
        public static void Main(string[] commandLineArgs)
        {
            if (GameKeySetup.HandleCommand(commandLineArgs)) return;
            bool created;
            mutex = new Mutex(true, "Local\\MicMuteBridge_Tray_v1", out created);
            if (!created)
            {
                MessageBox.Show("MicMuteBridge 已经在运行，请在右下角系统托盘查找图标。", "MicMuteBridge");
                mutex.Dispose();
                return;
            }
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (sender, args) =>
                    Diagnostics.WriteError("界面未处理异常：" + args.Exception.ToString());
                AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
                    Diagnostics.WriteError("进程未处理异常：" + args.ExceptionObject.ToString());
                string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string legacyFolder = String.IsNullOrEmpty(roaming) ? null : Path.Combine(roaming, "MicMuteBridge");
                LocalStorage.Current.Prepare(legacyFolder, Diagnostics.Write, line => Diagnostics.WriteError(line), true);
                Application.Run(new BridgeContext());
            }
            catch (Exception ex)
            {
                Diagnostics.WriteError("启动失败：" + ex.ToString());
                MessageBox.Show("MicMuteBridge 启动失败。\n" + ex.Message + "\n报错目录：" + LocalStorage.Current.ErrorsFolder,
                    "MicMuteBridge", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                mutex.ReleaseMutex();
                mutex.Dispose();
            }
        }
    }
}
