using System.Net;
using System.Text;
using RestoreDB.Domain.Models;

namespace RestoreDB.Services;

/// <summary>
/// Exporta o registro de operações da sessão para um arquivo HTML legível (para anexar a um
/// chamado / enviar ao DBA). Não lança: falha de exportação é reportada pelo retorno.
/// </summary>
public sealed class ReportService
{
    public void SaveHtmlReport(IEnumerable<RepairLogEntry> entries, string serverName, string filePath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html lang=\"pt-br\"><head><meta charset=\"utf-8\">");
        sb.AppendLine("<title>RestoreDB — Relatório de operações</title>");
        sb.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#222;}" +
                      "h1{color:#084466;} .meta{color:#5A6B7B;margin-bottom:16px;}" +
                      "table{border-collapse:collapse;width:100%;} th,td{border:1px solid #D7DEE5;padding:4px 8px;font-family:Consolas,monospace;font-size:12px;vertical-align:top;}" +
                      "th{background:#EEF3F7;text-align:left;}" +
                      ".Success{color:#1B7A2F;} .Warning{color:#B56A00;} .Error{color:#C01B1B;} .Command{color:#0B5A8A;} .Info{color:#333;}</style>");
        sb.AppendLine("</head><body>");
        sb.AppendLine("<h1>RestoreDB — Relatório de operações</h1>");
        sb.AppendLine($"<div class=\"meta\">Servidor: {Enc(serverName)} &nbsp;•&nbsp; Gerado em {DateTime.Now:yyyy-MM-dd HH:mm:ss}</div>");
        sb.AppendLine("<table><tr><th>Hora</th><th>Nível</th><th>Banco</th><th>Mensagem</th></tr>");

        foreach (var e in entries)
        {
            sb.AppendLine("<tr>" +
                $"<td>{e.Timestamp:HH:mm:ss}</td>" +
                $"<td class=\"{e.Level}\">{e.Level}</td>" +
                $"<td>{Enc(e.Database ?? string.Empty)}</td>" +
                $"<td class=\"{e.Level}\">{Enc(e.Message)}</td></tr>");
        }

        sb.AppendLine("</table></body></html>");
        File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
    }

    private static string Enc(string s) => WebUtility.HtmlEncode(s);
}
