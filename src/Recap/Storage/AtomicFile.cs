using System.IO;
using System.Text;
using System.Security.AccessControl;

namespace Recap.Storage;

public static class AtomicFile
{
    public static void WriteText(string path, string contents)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (File.Exists(path))
            {
                // Apply the old DACL before putting any credentials/private text in the staging file.
                var access = new FileInfo(path).GetAccessControl(AccessControlSections.Access);
                using (File.Create(temporary)) { }
                new FileInfo(temporary).SetAccessControl(access);
            }
            File.WriteAllText(temporary, contents, new UTF8Encoding(false));
            // Replace preserves the existing file's ACL (important for credential files).
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
