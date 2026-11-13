# Isolated Windows checks. Exercise copies and the desktop input registration without launching the tray app.
$ErrorActionPreference = 'Stop'
$path = $PSScriptRoot
$work = Join-Path $path ('.release check ' + [Guid]::NewGuid().ToString('N'))
$build = Join-Path $work 'BUILD.ps1'
$exe = Join-Path $work 'MicMuteBridge.exe'
$buildLog = Join-Path $work 'Errors\BUILD_ERROR.log'
$powershell = Join-Path $PSHOME 'powershell.exe'
$originalTemp = $env:TEMP
$originalTmp = $env:TMP

$checks = @'
namespace MicMuteBridge
{
    public static class ReleaseSettingsCheck
    {
        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        public static void Run(string path, string compiledApp, string soundSources)
        {
            var messages = new List<string>();
            string backup = path + ".bak";
            Settings fresh = Settings.LoadFrom(path, messages.Add);
            Require(fresh.FirstRun && fresh.WarmupMs == 300, "Fresh settings did not load defaults.");

            string micId = "{test-capture-endpoint}";
            File.WriteAllLines(path, new[] {
                "mic_id=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(micId)),
                "bridge=0", "sound=1", "sound_mode=2", "sound_volume_percent=63",
                "mute_trim_start=30", "mute_trim_end=140", "mute_preset=soft", "all_mics=0"
            }, Encoding.UTF8);
            Settings settings = Settings.LoadFrom(path, messages.Add);
            Require(!settings.FirstRun && settings.WarmupMs == 650, "Legacy warmup did not load.");
            Require(settings.MicId == micId && !settings.Bridge && !settings.AllMics &&
                settings.SoundVolumePercent == 63 && settings.MuteTrimStartMs == 30 &&
                settings.MuteTrimEndMs == 140 && settings.MutePreset == "soft", "Legacy preferences were lost.");

            settings.WarmupMs = 100;
            settings.SoundVolumePercent = 42;
            settings.SaveTo(path, messages.Add);
            string previous = File.ReadAllText(path, Encoding.UTF8);
            settings.SoundVolumePercent = 57;
            settings.SaveTo(path, messages.Add);
            string latest = File.ReadAllText(path, Encoding.UTF8);
            Require(File.ReadAllText(backup, Encoding.UTF8) == previous, "The previous settings were not backed up.");
            Require(Settings.LoadFrom(path, messages.Add).SoundVolumePercent == 57, "The new settings did not load.");
            settings.SaveTo(path, messages.Add);
            Require(File.ReadAllText(backup, Encoding.UTF8) == previous, "An unchanged save rotated the backup.");

            bool blocked = false;
            using (var held = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                settings.SoundVolumePercent = 99;
                try { settings.SaveTo(path, messages.Add); }
                catch (IOException) { blocked = true; }
                catch (UnauthorizedAccessException) { blocked = true; }
            }
            Require(blocked && File.ReadAllText(path, Encoding.UTF8) == latest,
                "A blocked save changed the active settings.");
            Require(File.ReadAllText(backup, Encoding.UTF8) == previous, "A blocked save lost the backup.");

            using (var held = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Require(Settings.LoadFrom(path, messages.Add).SoundVolumePercent == 42,
                    "An unreadable primary ignored the backup.");
            }
            File.WriteAllLines(path, new[] { "settings_format=1", "bridge=1", "sound_volume_percent=9" }, Encoding.UTF8);
            Settings restored = Settings.LoadFrom(path, messages.Add);
            Require(restored.MicId == micId && !restored.Bridge && !restored.AllMics &&
                restored.WarmupMs == 100 && restored.SoundVolumePercent == 42 &&
                restored.MuteTrimStartMs == 30 && restored.MuteTrimEndMs == 140,
                "Truncated settings did not recover from backup.");
            restored.SaveTo(path, messages.Add);
            Require(File.ReadAllText(backup, Encoding.UTF8) == previous, "Damaged settings replaced the good backup.");

            File.Delete(path);
            restored = Settings.LoadFrom(path, messages.Add);
            Require(restored.SoundVolumePercent == 42 && !restored.FirstRun, "A missing primary ignored the backup.");
            restored.SaveTo(path, messages.Add);
            Require(File.Exists(path) && File.ReadAllText(backup, Encoding.UTF8) == previous,
                "Recreating the primary changed the backup.");

            File.Delete(backup);
            File.WriteAllLines(path, new[] {
                "mic_id=invalid!", "bridge=0", "sound_warmup_ms=100", "sound_mode=1", "sound_volume_percent=37"
            }, Encoding.UTF8);
            Settings partial = Settings.LoadFrom(path, messages.Add);
            Require(partial.MicId == "" && !partial.Bridge && partial.WarmupMs == 100 &&
                partial.SoundVolumePercent == 37 && !partial.FirstRun, "A damaged field reset unrelated settings.");
            Require(messages.Any(message => message.Contains("settings.txt.bak")), "Backup recovery was not logged.");
            Require(Directory.GetFiles(Path.GetDirectoryName(path), "settings.txt.*.tmp").Length == 0,
                "A settings save left behind a temporary file.");

            restored.SaveTo(path, messages.Add);
            string[] rc1Lines = File.ReadAllLines(path, Encoding.UTF8)
                .Where(line => !line.StartsWith("desktop_shortcut=", StringComparison.Ordinal)).ToArray();
            File.WriteAllLines(path, rc1Lines, Encoding.UTF8);
            Settings oldFormat = Settings.LoadFrom(path, messages.Add);
            Require(oldFormat.WarmupMs == 100 && oldFormat.SoundVolumePercent == 42 &&
                !oldFormat.DesktopKeys.Enabled, "Adding an optional shortcut broke RC1 settings.");
            DesktopShortcut shortcut;
            Require(DesktopShortcut.TryCreate(DesktopShortcut.Control | DesktopShortcut.Alt, Keys.M, out shortcut),
                "Could not choose a valid desktop combination.");
            oldFormat.DesktopKeys = shortcut;
            oldFormat.SaveTo(path, messages.Add);
            Require(Settings.LoadFrom(path, messages.Add).DesktopKeys.Serialize() == "3,77",
                "The desktop combination did not round-trip.");
            Require(!DesktopShortcut.Parse("3,123").Enabled && !DesktopShortcut.Parse("8,77").Enabled &&
                !DesktopShortcut.Parse("broken").Enabled, "An unsupported desktop combination was accepted.");

            oldFormat.DesktopKeys = DesktopShortcut.Parse("chord:0101+0025+0024+0026");
            oldFormat.SaveTo(path, messages.Add);
            Settings recorded = Settings.LoadFrom(path, messages.Add);
            Require(recorded.DesktopKeys.Serialize() == "chord:0024+0025+0026+0101" &&
                recorded.WarmupMs == 100 && recorded.SoundVolumePercent == 42 && recorded.MicId == micId,
                "The recorded shortcut did not round-trip alongside the existing settings.");

            CheckDesktopChords();
            CheckGameKeys();
            CheckLocalStorage(Path.GetDirectoryName(path));
            CheckEmbeddedSounds(compiledApp, soundSources);
        }

        private static void CheckDesktopChords()
        {
            const ushort j = 0x0024, k = 0x0025, l = 0x0026, space = 0x0039;
            const ushort side = DesktopChord.MouseBack;
            DesktopChord letters = DesktopChord.Parse("0024+0025");
            Require(letters != null && !letters.UsesMouse && letters.Matches(new HashSet<ushort> { j, k }),
                "A modifier-free chord was rejected.");
            Require(DesktopChord.Parse("0102").UsesMouse && DesktopChord.Parse("0024+0101").UsesMouse,
                "A standalone or mixed mouse shortcut was rejected.");
            Require(!DesktopShortcut.Parse("chord:0024+0024").Enabled && !DesktopShortcut.Parse("chord:E020").Enabled &&
                !DesktopShortcut.Parse("chord:").Enabled && !DesktopShortcut.Parse("chord:0000").Enabled,
                "An invalid or speaker-mute shortcut was accepted.");

            var held = new HashSet<ushort>();
            var recorder = new DesktopChordRecording();
            foreach (ushort key in new[] { side, k, j, l })
            {
                held.Add(key);
                Require(recorder.Update(held, true) == null, "Recording finished before release.");
            }
            foreach (ushort key in new[] { side, j, l })
            {
                held.Remove(key);
                Require(recorder.Update(held, false) == null, "Recording finished during a partial release.");
            }
            held.Remove(k);
            DesktopChord mixed = recorder.Update(held, false);
            Require(mixed != null && mixed.UsesMouse && mixed.Serialize() == "0024+0025+0026+0101",
                "Recording lost a key from the four-input chord.");

            held.Add(DesktopChord.MouseForward);
            recorder.Update(held, true);
            held.Clear();
            DesktopChord mouseOnly = recorder.Update(held, false);
            Require(mouseOnly != null && mouseOnly.Serialize() == "0102", "A side button could not be recorded alone.");

            held.UnionWith(new[] { j, k });
            recorder.Update(held, true);
            held.Remove(j);
            recorder.Update(held, false);
            held.Add(l);
            recorder.Update(held, true);
            Require(recorder.Invalid, "Keys pressed during release were mixed into the recording.");
            held.Clear();
            Require(recorder.Update(held, false) == null, "An interrupted recording was saved.");

            held.Add(j);
            recorder.Update(held, true);
            held.Add(GameKeyMap.VolumeMute);
            recorder.Update(held, true);
            Require(recorder.Invalid, "Recording accepted the system mute key.");
            held.Remove(GameKeyMap.VolumeMute);
            recorder.Update(held, false);
            held.Clear();
            Require(recorder.Update(held, false) == null, "A rejected recording fell back to a partial shortcut.");

            var activation = new DesktopChordActivation();
            activation.Set(mixed, held);
            foreach (ushort key in new[] { j, l, side })
            {
                held.Add(key);
                Require(!activation.Update(held, true), "An incomplete chord triggered.");
            }
            held.Add(space);
            held.Add(k);
            Require(!activation.Update(held, true), "An extra key was ignored.");
            held.Remove(space);
            Require(!activation.Update(held, false), "Removing an extra key triggered a shortcut.");
            held.Remove(k);
            activation.Update(held, false);
            held.Add(k);
            Require(activation.Update(held, true) && !activation.Update(held, true),
                "The chord failed to trigger once while held.");
            held.Remove(j);
            activation.Update(held, false);
            held.Add(j);
            Require(!activation.Update(held, true), "A partial release rearmed the chord.");
            held.Clear();
            activation.Update(held, false);
            held.UnionWith(new[] { k, l, j, side });
            Require(activation.Update(held, true), "The chord did not rearm after release.");
            activation.Set(mixed, held);
            Require(!activation.Update(held, true), "Enabling a held shortcut triggered a toggle.");

            var inputErrors = new List<string>();
            using (var input = new DesktopRawInput(
                () => { throw new InvalidOperationException("Input registration triggered a toggle."); }, inputErrors.Add))
            {
                input.SetPaused(true);
                input.SetChord(letters);
                input.StartRecording(() => false, (chord, message) => { });
                input.StopRecording();
                input.SetChord(mixed);
                input.SetChord(null);
            }
            Require(inputErrors.Count == 0, "Windows raw input registration or cleanup failed: " + String.Join(", ", inputErrors.ToArray()));
        }

        private static void CheckEmbeddedSounds(string compiledApp, string soundSources)
        {
            Require(!Directory.Exists(Path.Combine(Path.GetDirectoryName(compiledApp), "Sounds")),
                "The embedded sound check must run without loose WAV files beside the EXE.");
            var assembly = System.Reflection.Assembly.Load(File.ReadAllBytes(compiledApp));
            var readCue = assembly.GetType("MicMuteBridge.SoundCatalog", true).GetMethod("ReadCue");
            foreach (string preset in SoundCatalog.Ids.Where(id => id != "custom"))
            {
                foreach (bool muted in new[] { true, false })
                {
                    string name = SoundCatalog.FileName(preset, muted);
                    byte[] audio = (byte[])readCue.Invoke(null, new object[] { preset, muted });
                    Require(audio.SequenceEqual(File.ReadAllBytes(Path.Combine(soundSources, name))),
                        "Embedded WAV differs from the original: " + name);

                    byte[] plain = WavPrep.Build(audio, 0, 90, 0, 0);
                    byte[] warmed = WavPrep.Build(audio, 100, 90, 0, 0);
                    const int leadBytes = 44100 / 10 * 4;
                    Require(warmed.Length - plain.Length == leadBytes && warmed.Skip(44).Take(leadBytes).All(value => value == 0) &&
                        warmed.Skip(44 + leadBytes).SequenceEqual(plain.Skip(44)), "100ms warmup changed the waveform: " + name);
                    int duration = WavPrep.DurationMs(audio);
                    byte[] trimmed = WavPrep.Build(audio, 0, 0, 10, 60);
                    Require(duration >= 220 && WavPrep.Peaks(audio, 120).Any(peak => peak > 0) &&
                        trimmed.Length == 44 + 44100 / 20 * 4 && trimmed.Skip(44).Any(value => value != 0),
                        "Embedded WAV could not be read for trimming: " + name);
                    WavPrep.ApplyVolume(trimmed, 0);
                    Require(trimmed.Skip(44).All(value => value == 0), "Cue volume did not apply to " + name);
                }
            }
        }

        private static void CheckLocalStorage(string work)
        {
            var messages = new List<string>();
            var errors = new List<string>();
            string legacy = Path.Combine(work, "old appdata");
            string legacySettings = Path.Combine(legacy, "settings.txt");
            string legacyLogs = Path.Combine(legacy, "logs");
            string legacySounds = Path.Combine(legacy, "Sounds");
            Directory.CreateDirectory(legacyLogs);
            Directory.CreateDirectory(legacySounds);
            var preferences = new Settings { WarmupMs = 100, SoundVolumePercent = 42, Bridge = false, MutePreset = "custom" };
            preferences.SaveTo(legacySettings, messages.Add);
            File.Copy(legacySettings, legacySettings + ".bak");
            string oldSound = Path.Combine(legacySounds, "custom_mute.wav");
            var samples = new byte[] { 1, 2, 3, 4 };
            File.WriteAllBytes(oldSound, samples);
            string oldLog = Path.Combine(legacyLogs, "MicMuteBridge.log");
            File.WriteAllText(oldLog, "old runtime log");
            File.WriteAllText(Path.Combine(legacyLogs, "MicMuteBridge.previous.log"), "previous runtime log");
            File.WriteAllText(Path.Combine(legacy, "mute-recovery.txt"), "diagnostic only");

            var local = new LocalStorage(Path.Combine(work, "portable app"));
            local.Prepare(legacy, messages.Add, errors.Add);
            string localSettings = Path.Combine(local.SettingsFolder, "settings.txt");
            Settings migrated = Settings.LoadFrom(localSettings, messages.Add);
            Require(migrated.WarmupMs == 100 && migrated.SoundVolumePercent == 42 && !migrated.Bridge && migrated.MutePreset == "custom",
                "Legacy settings changed during migration.");
            Require(File.ReadAllBytes(localSettings + ".bak").SequenceEqual(File.ReadAllBytes(legacySettings + ".bak")),
                "The legacy settings backup was not copied.");
            Require(File.ReadAllBytes(Path.Combine(local.SettingsFolder, "Sounds", "custom_mute.wav")).SequenceEqual(samples) &&
                File.ReadAllText(Path.Combine(local.SettingsFolder, "mute-recovery.txt")) == "diagnostic only",
                "Related settings files were not copied with the preferences.");
            Require(File.ReadAllText(Path.Combine(local.LogsFolder, "Legacy", "MicMuteBridge.log")) == "old runtime log" &&
                File.ReadAllText(Path.Combine(local.LogsFolder, "Legacy", "MicMuteBridge.previous.log")) == "previous runtime log" &&
                !File.Exists(Path.Combine(local.LogsFolder, "MicMuteBridge.log")), "Legacy logs were not archived separately.");
            Require(File.Exists(legacySettings) && File.Exists(oldSound) && File.Exists(oldLog) &&
                Directory.Exists(local.ErrorsFolder) && errors.Count == 0, "Migration changed the legacy copy or missed a local folder.");

            migrated.SoundVolumePercent = 70;
            migrated.SaveTo(localSettings, messages.Add);
            local.Prepare(legacy, messages.Add, errors.Add);
            Require(Settings.LoadFrom(localSettings, messages.Add).SoundVolumePercent == 70 &&
                Settings.LoadFrom(legacySettings, messages.Add).SoundVolumePercent == 42, "A second launch imported over local settings.");

            string interruptedRoot = Path.Combine(work, "interrupted migration");
            var interrupted = new LocalStorage(interruptedRoot);
            bool blocked = false;
            using (var held = File.Open(oldSound, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                try { interrupted.Prepare(legacy, messages.Add, errors.Add); }
                catch (IOException) { blocked = true; }
            }
            Require(blocked && !Directory.Exists(interrupted.SettingsFolder) &&
                Directory.GetDirectories(interruptedRoot, "Settings.import-*").Length == 0,
                "A failed migration published incomplete settings.");
            interrupted.Prepare(legacy, messages.Add, errors.Add);
            Require(File.Exists(Path.Combine(interrupted.SettingsFolder, "settings.txt")), "The migration could not be retried.");

            var withoutHistory = new LocalStorage(Path.Combine(work, "locked log"));
            using (var held = File.Open(oldLog, FileMode.Open, FileAccess.Read, FileShare.None))
                withoutHistory.Prepare(legacy, messages.Add, errors.Add);
            Require(errors.Count == 1 && File.Exists(Path.Combine(withoutHistory.SettingsFolder, "settings.txt")) &&
                !Directory.Exists(Path.Combine(withoutHistory.LogsFolder, "Legacy")), "An unavailable history log interfered with settings migration.");

            var fresh = new LocalStorage(Path.Combine(work, "fresh portable app"));
            fresh.Prepare(null, messages.Add, errors.Add);
            Require(Directory.Exists(fresh.SettingsFolder) && Directory.Exists(fresh.LogsFolder) && Directory.Exists(fresh.ErrorsFolder) &&
                Settings.LoadFrom(Path.Combine(fresh.SettingsFolder, "settings.txt"), messages.Add).FirstRun,
                "A fresh local install did not start with the normal defaults.");
        }

        private static void CheckGameKeys()
        {
            const ushort home = 0xE047, f10 = 0x0044;
            const uint unrelated = 0x001D003A;
            uint homeMute = GameKeyMap.MuteEntry(home);
            uint f10Mute = GameKeyMap.MuteEntry(f10);
            Require(homeMute == 0xE047E020u && f10Mute == 0x0044E020u, "Unexpected scan-code encoding.");
            var entries = new List<uint> { unrelated, homeMute };
            var owned = new HashSet<ushort> { home };
            Require(GameKeyMap.Decode(GameKeyMap.Encode(entries)).SequenceEqual(entries),
                "The scan-code map did not round-trip.");
            List<uint> changed = GameKeyMap.Plan(entries, owned, f10);
            Require(changed.SequenceEqual(new[] { unrelated, f10Mute }), "Replacing the owned Home key changed another key.");
            Require(GameKeyMap.Plan(changed, new HashSet<ushort> { f10 }, null).SequenceEqual(new[] { unrelated }),
                "Undo changed another keyboard mapping.");
            Require(GameKeyMap.Plan(entries, new HashSet<ushort>(), null).SequenceEqual(entries),
                "Undo removed an external mapping.");
            bool conflict = false;
            try { GameKeyMap.Plan(entries, new HashSet<ushort>(), home); }
            catch (InvalidOperationException) { conflict = true; }
            Require(conflict, "An existing external Home mapping was accepted for replacement.");
            bool rejected = false;
            try { GameKeyMap.Decode(GameKeyMap.Encode(new[] { homeMute, homeMute })); }
            catch (InvalidDataException) { rejected = true; }
            Require(rejected, "A duplicate source key was accepted.");

            var keypadMessages = new Dictionary<string, int> {
                { "Numpad0", 0x00520001 }, { "Numpad1", 0x004F0001 }, { "Numpad2", 0x00500001 },
                { "Numpad3", 0x00510001 }, { "Numpad4", 0x004B0001 }, { "Numpad5", 0x004C0001 },
                { "Numpad6", 0x004D0001 }, { "Numpad7", 0x00470001 }, { "Numpad8", 0x00480001 },
                { "Numpad9", 0x00490001 }, { "NumpadMultiply", 0x00370001 }, { "NumpadDivide", 0x01350001 },
                { "NumpadAdd", 0x004E0001 }, { "NumpadSubtract", 0x004A0001 },
                { "NumpadDecimal", 0x00530001 }, { "NumpadEnter", 0x011C0001 }
            };
            foreach (var key in keypadMessages)
            {
                ushort down = GameKeyMap.ScanCodeFromMessage(key.Value);
                ushort up = GameKeyMap.ScanCodeFromMessage(key.Value | unchecked((int)0xC0000000));
                Require(down == up && GameKeyMap.Choices.Single(choice => choice.ScanCode == down).Name == key.Key,
                    "Keypad press and release did not identify " + key.Key + ".");
            }
            Require(GameKeyMap.ScanCodeFromMessage(0x01470001) == home, "Dedicated Home was mistaken for keypad 7.");
            Require(GameKeyMap.Choices.All(choice => choice.ScanCode != GameKeyMap.ScanCodeFromMessage(0x01370001)),
                "Print Screen was mistaken for keypad multiply.");

            const ushort keypad7 = 0x0047;
            const uint keypad7Mute = 0x0047E020u;
            List<uint> keypad = GameKeyMap.Plan(entries, owned, keypad7);
            Require(keypad.SequenceEqual(new[] { unrelated, keypad7Mute }), "Switching Home to keypad 7 changed another key.");
            Require(GameKeyMap.Plan(keypad, new HashSet<ushort> { keypad7 }, null).SequenceEqual(new[] { unrelated }),
                "Undo did not remove just the keypad mapping.");
            Require(GameKeyMap.Plan(entries, new HashSet<ushort>(), keypad7).SequenceEqual(new[] { unrelated, homeMute, keypad7Mute }),
                "Selecting keypad 7 changed an external Home mapping.");

            CheckRecordedKeys(entries, owned, unrelated);
        }

        private static void CheckRecordedKeys(List<uint> entries, HashSet<ushort> owned, uint unrelated)
        {
            var examples = new[] {
                new { Key = Keys.A, Data = 0x001E0001, Code = (ushort)0x001E },
                new { Key = Keys.D1, Data = 0x00020001, Code = (ushort)0x0002 },
                new { Key = Keys.Oem1, Data = 0x00270001, Code = (ushort)0x0027 },
                new { Key = Keys.Space, Data = 0x00390001, Code = (ushort)0x0039 },
                new { Key = Keys.Escape, Data = 0x00010001, Code = (ushort)0x0001 },
                new { Key = Keys.Tab, Data = 0x000F0001, Code = (ushort)0x000F },
                new { Key = Keys.ControlKey, Data = 0x001D0001, Code = (ushort)0x001D },
                new { Key = Keys.ControlKey, Data = 0x011D0001, Code = (ushort)0xE01D },
                new { Key = Keys.ShiftKey, Data = 0x00360001, Code = (ushort)0x0036 },
                new { Key = Keys.F24, Data = 0x00760001, Code = (ushort)0x0076 },
                new { Key = Keys.PrintScreen, Data = 0x01370001, Code = (ushort)0xE037 },
                new { Key = Keys.NumLock, Data = 0x01450001, Code = (ushort)0x0045 }
            };
            foreach (var example in examples)
            {
                ushort down = GameKeyMap.RecordableScanCode(example.Key, example.Data);
                ushort up = GameKeyMap.RecordableScanCode(example.Key, example.Data | unchecked((int)0xC0000000));
                Require(down == example.Code && up == down, "Physical key was decoded incorrectly: " + example.Key);
                GameKeyOption choice = GameKeyMap.FromScanCode(down);
                ushort parsed;
                Require(choice != null && choice.ToString().Length != 0 &&
                    GameKeyMap.TryGetScanCode(choice.Name, out parsed) && parsed == down,
                    "A recorded key did not round-trip through its command identifier.");
                Require(GameKeySetup.TryGetMarkerScanCode("GameKey_" + choice.Name, out parsed) && parsed == down,
                    "A recorded key was not recognized for undo.");
            }
            Require(GameKeyMap.RecordableScanCode(Keys.Pause, 0x00450001) == 0 &&
                GameKeyMap.RecordableScanCode(Keys.Cancel, 0x01460001) == 0 &&
                GameKeyMap.RecordableScanCode(Keys.Packet, 0x001E0001) == 0 &&
                GameKeyMap.RecordableScanCode(Keys.VolumeMute, 0x01200001) == 0 &&
                GameKeyMap.RecordableScanCode(Keys.A, 1) == 0,
                "An unsupported key could be assigned to the system map.");
            Require(GameKeyMap.RecordableScanCode(Keys.PrintScreen, 0x00540001) == 0 &&
                GameKeyMap.CanCaptureOnRelease(0xE037) && !GameKeyMap.CanCaptureOnRelease(0x0037),
                "Print Screen and keypad multiply were not distinguished.");
            Require(GameKeyMap.RecordableScanCode(Keys.HanjaMode, 0x00F10001) == 0x0071 &&
                GameKeyMap.RecordableScanCode(Keys.HangulMode, 0x00F20001) == 0x0072 &&
                GameKeyMap.CanCaptureOnRelease(0x0071) && GameKeyMap.CanCaptureOnRelease(0x0072),
                "Legacy language key codes did not normalize for release-only capture.");

            ushort source;
            Require(GameKeySetup.TryGetMarkerScanCode("OriginalMapPresent", out source) && source == 0xE047,
                "The legacy Home mapper record was not recognized.");
            foreach (GameKeyOption preset in GameKeyMap.Choices)
                Require(GameKeySetup.TryGetMarkerScanCode("GameKey_" + preset.Name, out source) && source == preset.ScanCode,
                    "A saved preset mapping was not recognized: " + preset.Name);
            Require(!GameKeyMap.TryGetScanCode("Scan_0000", out source) && !GameKeyMap.TryGetScanCode("Scan_E11D", out source) &&
                !GameKeyMap.TryGetScanCode("Scan_E020", out source) && !GameKeyMap.TryGetScanCode("Scan_001E extra", out source),
                "An invalid recorded identifier was accepted.");

            const ushort letter = 0x001E, rightControl = 0xE01D;
            List<uint> letterMap = GameKeyMap.Plan(entries, owned, letter);
            Require(letterMap.SequenceEqual(new[] { unrelated, 0x001EE020u }), "Replacing Home with a recorded key changed another mapping.");
            List<uint> rightControlMap = GameKeyMap.Plan(letterMap, new HashSet<ushort> { letter }, rightControl);
            Require(rightControlMap.SequenceEqual(new[] { unrelated, 0xE01DE020u }), "Replacing a recorded key changed the left Ctrl mapping.");
            Require(GameKeyMap.Plan(rightControlMap, new HashSet<ushort> { rightControl }, null).SequenceEqual(new[] { unrelated }),
                "Undo of a recorded key removed another mapping.");
        }
    }
}
'@

try {
    [void][System.IO.Directory]::CreateDirectory($work)
    $env:TEMP = $work
    $env:TMP = $work
    Copy-Item -LiteralPath (Join-Path $path 'BUILD.ps1') -Destination $work
    Copy-Item -LiteralPath (Join-Path $path 'MicMuteBridge.cs') -Destination $work
    Copy-Item -LiteralPath (Join-Path $path 'Sounds') -Destination $work -Recurse

    & $powershell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $build
    if ($LASTEXITCODE -ne 0) { throw 'The first build failed.' }
    & $powershell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $build
    if ($LASTEXITCODE -ne 0) { throw 'Replacing the existing build failed.' }
    $originalHash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
    [System.IO.File]::AppendAllText((Join-Path $work 'MicMuteBridge.cs'),
        ([Environment]::NewLine + '#error INTENTIONAL_RELEASE_CHECK' + [Environment]::NewLine),
        [System.Text.Encoding]::UTF8)
    $failureOutput = & $powershell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $build
    if ($LASTEXITCODE -eq 0) { throw 'The deliberate compiler error unexpectedly succeeded.' }
    if ((Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash -ne $originalHash) {
        throw 'A failed build changed the previous EXE.'
    }
    if (-not [System.IO.File]::Exists($buildLog)) { throw ('The failed build left no log. ' + ($failureOutput -join ' ')) }
    Remove-Item -LiteralPath $buildLog
    Write-Host 'PASS: build, rebuild, and preservation of the previous EXE.' -ForegroundColor Green

    Copy-Item -LiteralPath (Join-Path $path 'MicMuteBridge.cs') -Destination $work -Force
    $soundSources = Join-Path $work 'WAV inputs'
    [System.IO.Directory]::Move((Join-Path $work 'Sounds'), $soundSources)
    $missingSoundOutput = & $powershell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $build
    if ($LASTEXITCODE -eq 0) { throw 'A build with missing WAVs unexpectedly succeeded.' }
    if ((Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash -ne $originalHash) {
        throw 'Missing WAVs changed the previous EXE.'
    }
    if (-not [System.IO.File]::Exists($buildLog) -or -not [System.IO.File]::ReadAllText($buildLog).Contains('Missing Sounds\classic_mute.wav')) {
        throw ('The failed build did not identify the missing WAV. ' + ($missingSoundOutput -join ' '))
    }
    Remove-Item -LiteralPath $buildLog
    Write-Host 'PASS: missing WAVs are reported and the previous EXE is preserved.' -ForegroundColor Green

    $source = [System.IO.File]::ReadAllText((Join-Path $path 'MicMuteBridge.cs'), [System.Text.Encoding]::UTF8)
    Add-Type -TypeDefinition ($source + [Environment]::NewLine + $checks) -ReferencedAssemblies @('System.dll', 'System.Core.dll', 'System.Windows.Forms.dll', 'System.Drawing.dll')
    [MicMuteBridge.ReleaseSettingsCheck]::Run((Join-Path $work 'settings.txt'), $exe, $soundSources)
    Write-Host 'PASS: local storage, legacy settings, recorded chords, and mapping rollback.' -ForegroundColor Green
    Write-Host 'PASS: all embedded WAVs are unchanged and prepare without an external Sounds folder.' -ForegroundColor Green
    Write-Host 'RELEASE CHECK PASSED' -ForegroundColor Green
} catch {
    $detail = $_.Exception.ToString()
    if ([System.IO.File]::Exists($buildLog)) {
        $detail += [Environment]::NewLine + [System.IO.File]::ReadAllText($buildLog, [System.Text.Encoding]::UTF8)
    }
    try {
        $errors = Join-Path $path 'Errors'
        [void][System.IO.Directory]::CreateDirectory($errors)
        [System.IO.File]::WriteAllText((Join-Path $errors 'CHECK_RELEASE_ERROR.log'), $detail, [System.Text.Encoding]::UTF8)
    } catch {
        Write-Host ('Could not write CHECK_RELEASE_ERROR.log: ' + $_.Exception.Message) -ForegroundColor Red
    }
    Write-Host $detail -ForegroundColor Red
    exit 1
} finally {
    $env:TEMP = $originalTemp
    $env:TMP = $originalTmp
    if ([System.IO.Directory]::Exists($work)) {
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}
