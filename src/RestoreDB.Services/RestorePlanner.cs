using RestoreDB.Domain.Constants;
using RestoreDB.Domain.Models;

namespace RestoreDB.Services;

/// <summary>
/// Decide a realocação de arquivos (WITH MOVE) numa restauração. Lógica PURA (sem banco):
/// dado os arquivos do backup (FILELISTONLY), o conjunto de pastas que EXISTEM no servidor e as
/// pastas padrão da instância, devolve o mapa nome-lógico → novo-caminho APENAS para os arquivos
/// que precisam mudar (pasta original ausente, ou realocação forçada — caso de cópia/salvamento).
/// </summary>
public static class RestorePlanner
{
    public static Dictionary<string, string> BuildMoveMap(
        IReadOnlyList<BackupFileInfo> files,
        ISet<string> existingDirs,
        string defaultDataDir,
        string defaultLogDir,
        string targetDbName,
        bool forceRelocate)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dataIdx = 0;

        foreach (var f in files)
        {
            var isLog = f.Type.Equals("L", StringComparison.OrdinalIgnoreCase);
            var ext = isLog ? ".ldf" : (dataIdx++ == 0 ? ".mdf" : ".ndf");

            var originalDir = SafeDir(f.PhysicalName);
            var dirOk = originalDir.Length > 0 && existingDirs.Contains(originalDir);

            var needsMove = forceRelocate || !dirOk;
            if (!needsMove) continue;

            var destDir = isLog ? defaultLogDir : defaultDataDir;
            if (string.IsNullOrEmpty(destDir)) destDir = defaultDataDir;            // fallback
            if (string.IsNullOrEmpty(destDir)) continue;                            // sem destino: não move

            var fileName = $"{AppConstants.SanitizeToken(targetDbName, "f")}_{AppConstants.SanitizeToken(f.LogicalName, "f")}_{map.Count}{ext}";
            map[f.LogicalName] = Path.Combine(destDir, fileName);
        }

        return map;
    }

    private static string SafeDir(string physical)
    {
        try { return Path.GetDirectoryName(physical) ?? string.Empty; }
        catch { return string.Empty; }
    }
}
