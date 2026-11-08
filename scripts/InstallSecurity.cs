using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;

namespace KeySwap.Installation
{
    public static class InstallSecurity
    {
        // No ACLs are changed here. In particular, never relabel the source
        // workspace: its Low integrity label is an intentional sandbox boundary.
        public static string ValidateDirectory(string path)
        {
            string fullPath = Normalize(path);
            ValidatePath(fullPath, false);
            CheckDirectoryLabels(fullPath);
            Directory.CreateDirectory(fullPath);
            ValidatePath(fullPath, true);
            CheckDirectoryLabels(fullPath);
            return fullPath;
        }

        public static void ValidatePath(string path, bool mustExist)
        {
            string fullPath = Normalize(path);
            string root = Path.GetPathRoot(fullPath);
            string current = root;
            string[] components = fullPath.Substring(root.Length).Split(new char[] { Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            for (int index = -1; index < components.Length; index++)
            {
                if (index >= 0) current = Path.Combine(current, components[index]);
                FileAttributes attributes;
                if (!TryAttributes(current, out attributes))
                {
                    if (mustExist) throw new FileNotFoundException("Required installation path does not exist.", fullPath);
                    return;
                }
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Installation paths must not traverse a reparse point: " + current);
                if (index < components.Length - 1 && (attributes & FileAttributes.Directory) == 0)
                    throw new IOException("Installation path has a non-directory ancestor: " + current);
            }
        }

        public static int GetFileIntegrityRid(string path)
        {
            string fullPath = Normalize(path);
            ValidatePath(fullPath, true);
            IntPtr owner, group, dacl, sacl, descriptor;
            // LABEL_SECURITY_INFORMATION can read the mandatory label without
            // requesting the privileged audit SACL (SACL_SECURITY_INFORMATION).
            uint error = GetNamedSecurityInfo(fullPath, 1, 0x10, out owner, out group, out dacl, out sacl, out descriptor);
            if (error != 0) throw new Win32Exception((int)error, "Cannot inspect installation integrity: " + fullPath);
            try
            {
                if (sacl == IntPtr.Zero) return 8192; // Unlabeled objects have Medium integrity.
                AclSizeInformation information;
                if (!GetAclInformation(sacl, out information, (uint)Marshal.SizeOf(typeof(AclSizeInformation)), 2))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                int result = int.MaxValue;
                for (uint index = 0; index < information.AceCount; index++)
                {
                    IntPtr ace;
                    if (!GetAce(sacl, index, out ace)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    if (Marshal.ReadByte(ace) != 0x11) continue; // SYSTEM_MANDATORY_LABEL_ACE
                    SecurityIdentifier sid = new SecurityIdentifier(IntPtr.Add(ace, 8));
                    string value = sid.Value;
                    if (!value.StartsWith("S-1-16-", StringComparison.Ordinal))
                        throw new IOException("Unrecognized mandatory integrity SID: " + value);
                    int rid;
                    if (!int.TryParse(value.Substring(7), out rid)) throw new IOException("Invalid integrity SID: " + value);
                    result = Math.Min(result, rid);
                }
                return result == int.MaxValue ? 8192 : result;
            }
            finally { if (descriptor != IntPtr.Zero) LocalFree(descriptor); }
        }

        public static string StageExecutable(string source, string destinationDirectory)
        {
            source = Normalize(source);
            ValidatePath(source, true);
            if ((File.GetAttributes(source) & FileAttributes.Directory) != 0) throw new IOException("Executable source is a directory.");
            string directory = ValidateDirectory(destinationDirectory);
            string target = Path.Combine(directory, ".keyswap-stage-" + Guid.NewGuid().ToString("N") + ".exe");
            bool created = false;
            try
            {
                // File.Copy/Copy-Item can preserve source security metadata. Create
                // a NEW destination-owned file and copy bytes only, inheriting the
                // destination ACL instead of the workspace's Low integrity label.
                using (FileStream input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (FileStream output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    created = true;
                    input.CopyTo(output);
                    output.Flush(true);
                }
                ValidatePath(target, true);
                CheckDirectoryLabels(directory);
                if (GetFileIntegrityRid(target) < 8192) throw new UnauthorizedAccessException("Fresh executable inherited Low integrity: " + target);
                if (!string.Equals(Sha256(source), Sha256(target), StringComparison.Ordinal))
                    throw new IOException("Staged executable SHA256 does not match its source.");
                return target;
            }
            catch
            {
                // Delete only the unique file created by this operation. Recheck
                // its path first; never follow a replacement junction for cleanup.
                if (created)
                {
                    try { ValidatePath(target, true); File.Delete(target); }
                    catch { }
                }
                throw;
            }
        }
        public static string Sha256(string path)
        {
            using (SHA256 algorithm = SHA256.Create())
            using (FileStream input = File.OpenRead(path)) return BitConverter.ToString(algorithm.ComputeHash(input)).Replace("-", "");
        }

        private static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("An absolute local installation path is required.", "path");
            path = path.Replace('/', '\\');
            if (path.Length < 3 || !char.IsLetter(path[0]) || path[1] != ':' || path[2] != '\\')
                throw new ArgumentException("Only absolute local drive paths are supported.", "path");
            if (path.IndexOf(':', 2) >= 0 || path.IndexOf('*') >= 0 || path.IndexOf('?') >= 0)
                throw new ArgumentException("Alternate streams and wildcard paths are not installation paths.", "path");
            string fullPath = Path.GetFullPath(path);
            foreach (string component in fullPath.Substring(3).Split('\\'))
            {
                if (component.EndsWith(".", StringComparison.Ordinal) || component.EndsWith(" ", StringComparison.Ordinal))
                    throw new ArgumentException("Installation paths must not use trailing-dot or trailing-space aliases.", "path");
                string stem = component.Split('.')[0].ToUpperInvariant();
                if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL"
                    || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] >= '1' && stem[3] <= '9'))
                    throw new ArgumentException("Device names are not installation paths.", "path");
            }
            return fullPath.Length > 3 ? fullPath.TrimEnd('\\') : fullPath;
        }

        private static bool TryAttributes(string path, out FileAttributes attributes)
        {
            try { attributes = File.GetAttributes(path); return true; }
            catch (FileNotFoundException) { attributes = 0; return false; }
            catch (DirectoryNotFoundException) { attributes = 0; return false; }
        }

        private static void CheckDirectoryLabels(string path)
        {
            string current = path;
            while (!string.IsNullOrEmpty(current))
            {
                FileAttributes attributes;
                if (TryAttributes(current, out attributes))
                {
                    if ((attributes & FileAttributes.Directory) == 0) throw new IOException("Installation destination is not a directory: " + current);
                    if (GetFileIntegrityRid(current) < 8192)
                        throw new UnauthorizedAccessException("Installation destination inherits Low integrity; choose a normal directory outside the sandbox workspace: " + current);
                }
                DirectoryInfo parent = Directory.GetParent(current);
                current = parent == null ? null : parent.FullName;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AclSizeInformation { public uint AceCount, AclBytesInUse, AclBytesFree; }

        [DllImport("advapi32.dll", EntryPoint = "GetNamedSecurityInfoW", CharSet = CharSet.Unicode)]
        private static extern uint GetNamedSecurityInfo(string name, int type, uint information, out IntPtr owner,
            out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetAclInformation(IntPtr acl, out AclSizeInformation information, uint length, int informationClass);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetAce(IntPtr acl, uint index, out IntPtr ace);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    }
}
