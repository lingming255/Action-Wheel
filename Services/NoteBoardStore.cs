using System;
using System.IO;
using System.Text;
using Action_Wheel.Core;

namespace Action_Wheel.Services
{
    /// <summary>Owns one loaded draft; a failed read never turns into permission to overwrite it.</summary>
    public sealed class NoteBoardStore
    {
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private bool _loaded;
        private bool _fileExisted;
        private string _savedText = string.Empty;

        public NoteBoardStore(string? path = null)
        {
            FilePath = path ?? Path.Combine(AppDataPaths.DirectoryPath, "noteboard.txt");
        }

        public string FilePath { get; private set; }

        internal void SetLoadPath(string path)
        {
            FilePath = Path.GetFullPath(path);
            _loaded = false;
        }

        public bool TryLoad(out string text, out string error)
        {
            _loaded = false;
            text = string.Empty;
            error = string.Empty;
            try
            {
                _fileExisted = TryRead(out text);
                _savedText = text;
                _loaded = true;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public bool TrySave(string text, out string error)
        {
            error = string.Empty;
            if (!_loaded)
            {
                error = "笔记读取尚未成功，已阻止覆盖原文件。";
                return false;
            }

            // An unchanged editor has nothing to commit. In particular, do not create an empty
            // file just because a first-time user opened and immediately closed the board.
            if (string.Equals(text, _savedText, StringComparison.Ordinal))
                return true;

            try
            {
                bool exists = TryRead(out string current);
                if (exists != _fileExisted || !string.Equals(current, _savedText, StringComparison.Ordinal))
                {
                    error = "笔记文件已被其他程序修改。当前输入仍保留在窗口中，请先复制保留，再重新读取文件。";
                    return false;
                }

                if (!AtomicFile.TryWriteText(FilePath, text, out error))
                    return false;
                _savedText = text;
                _fileExisted = true;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>A recovery file never replaces either the live draft or another recovery.</summary>
        public bool TrySaveCopy(string text, out string copyPath, out string error)
        {
            copyPath = Path.Combine(Path.GetDirectoryName(FilePath)!, "RecoveredNotes",
                $"noteboard-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.txt");
            return AtomicFile.TryWriteText(copyPath, text, out error);
        }

        /// <summary>Only switches the live file after both its contents and location are durable.</summary>
        public bool TrySaveAs(string path, string text, bool rememberLocation, out string error,
            string? locationSettingsPath = null)
        {
            error = string.Empty;
            if (!_loaded)
            {
                error = "笔记读取尚未成功，已阻止另存空白内容。";
                return false;
            }
            try
            {
                path = Path.GetFullPath(path);
                if (rememberLocation && string.Equals(path,
                    Path.GetFullPath(locationSettingsPath ?? NoteBoardLocationSettings.FilePath),
                    StringComparison.OrdinalIgnoreCase))
                {
                    error = "笔记文件不能覆盖用于记住保存位置的设置文件。";
                    return false;
                }
                // FileSavePicker owns overwrite confirmation. AtomicFile also retains any prior
                // destination as .bak; a location-settings failure leaves this as a safe copy.
                if (!AtomicFile.TryWriteText(path, text, out error))
                    return false;
                bool currentTarget = string.Equals(path, Path.GetFullPath(FilePath), StringComparison.OrdinalIgnoreCase);
                if (currentTarget)
                {
                    // The active file has already changed, even if persisting its location fails.
                    // Keep its baseline current so the next autosave does not mistake our own
                    // successful write for an external edit and block further input.
                    _savedText = text;
                    _fileExisted = _loaded = true;
                }
                if (rememberLocation && !NoteBoardLocationSettings.TrySave(path, out error, locationSettingsPath))
                {
                    error = $"内容已另存到 {path}，但记住位置失败，当前保存位置没有切换：{error}";
                    return false;
                }
                FilePath = path;
                _savedText = text;
                _fileExisted = _loaded = true;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private bool TryRead(out string text)
        {
            try
            {
                text = File.ReadAllText(FilePath, StrictUtf8);
                return true;
            }
            catch (FileNotFoundException)
            {
                text = string.Empty;
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                text = string.Empty;
                return false;
            }
        }
    }
}
