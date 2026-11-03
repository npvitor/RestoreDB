# RestoreDB — Diagnóstico e reparo de bancos SQL Server

Aplicativo para Windows (WPF, .NET 10) que **diagnostica e repara bancos SQL Server com problema**:
banco `SUSPECT`, `RECOVERY_PENDING`, `OFFLINE`, preso em `SINGLE_USER`, log de transações cheio,
arquivo ausente, corrupção física (páginas danificadas), registros órfãos de chave estrangeira,
exclusão acidental de dados.

Foi feito para **equipes de suporte sem conhecimento avançado de SQL Server**: a tela conduz o
trabalho em três passos — **Diagnosticar → Resolver → Conferir** —, mostra só as opções que se
aplicam ao que foi encontrado, em **ordem de risco**, e exige confirmação explícita antes de
qualquer operação que possa perder dados. Cada comando executado aparece em tempo real no
**Registro de operações** e fica gravado em arquivo.

> 📘 **Manual de uso** (passo a passo para o suporte, linguagem simples):
> [docs/MANUAL.md](docs/MANUAL.md)

---

## Sumário

- [Para quem é](#para-quem-é)
- [Como é a tela](#como-é-a-tela)
- [O que a ferramenta faz](#o-que-a-ferramenta-faz)
- [Requisitos](#requisitos)
- [Baixar, compilar e publicar](#baixar-compilar-e-publicar)
- [Segurança e limites](#segurança-e-limites)
- [Monitoramento agendado (RestoreDB.Probe)](#monitoramento-agendado-restoredbprobe)
- [Testes](#testes)
- [Estrutura do projeto](#estrutura-do-projeto)
- [Licença](#licença)

---

## Para quem é

- **Suporte técnico / implantação** que atende clientes com SQL Server local (inclusive Express) e
  precisa colocar um banco de volta no ar sem um DBA disponível.
- **DBAs** que querem um roteiro padronizado, com registro de tudo o que foi executado, para
  anexar a um chamado.

Não é uma ferramenta de administração geral do SQL Server, nem substitui uma política de backup.

## Como é a tela

Depois da tela de conexão (servidor, autenticação do Windows ou usuário/senha SQL), a janela
principal tem cinco áreas:

```
┌──────────────────────────────────────────────────────────────────────────────────────┐
│ RestoreDB · Servidor / versão / sysadmin     [⟳ Atualizar lista] [📎 Anexar .mdf]    │
│                                              [Trocar conexão] [🌙 Tema escuro]       │
├───────────────────┬──────────────────────────────────────────────────────────────────┤
│ Bancos de dados   │  Banco selecionado · estado · ① Diagnosticar ② Resolver ③ Conferir│
│ 🔍 Filtrar...     │  PRÓXIMO PASSO: o que fazer agora                                 │
│ ─────────────     │ ┌──── 1 Diagnosticar ─────┐ ┌──── 2 Resolver ──────────────────┐ │
│ Banco    Estado   │ │ 🔍 Diagnosticar (sem    │ │ Risco: SEM PERDA → QUARENTENA →  │ │
│ (vermelho =       │ │    alterar o banco)     │ │  RESTAURA BACKUP → PODE PERDER   │ │
│  problema,        │ │ Resultado do diagnóstico│ │ Cartões das opções que se aplicam│ │
│  amarelo =        │ └─────────────────────────┘ └──────────────────────────────────┘ │
│  travado)         │ Ferramentas adicionais: lista à esquerda, detalhe à direita       │
├───────────────────┴──────────────────────────────────────────────────────────────────┤
│ Registro de operações — cada comando executado        [Exportar relatório] [Limpar] │
├──────────────────────────────────────────────────────────────────────────────────────┤
│ Barra de status · [■ Cancelar operação] · caminho do arquivo de log                  │
└──────────────────────────────────────────────────────────────────────────────────────┘
```

- **Lista de bancos** (esquerda): todos os bancos da instância com o estado. Linhas em vermelho
  estão em estado problemático; em amarelo, estão `ONLINE` mas travados (acesso restrito ou
  somente leitura). Um banco `ONLINE` também pode ter corrupção — só o diagnóstico revela.
- **Passo 1 — Diagnosticar**: verificação completa e somente leitura.
- **Passo 2 — Resolver**: só aparece depois do diagnóstico, com as opções aplicáveis.
- **Passo 3 — Conferir**: depois de uma correção, a ferramenta pede um novo diagnóstico.
- **Ferramentas adicionais**: verificar uma tabela, manutenção preventiva, verificação em partes
  e recuperação de exclusão acidental — independentes do fluxo de reparo.
- **Tema claro/escuro**: segue o tema do Windows ao abrir; alterna pelo botão do cabeçalho.

## O que a ferramenta faz

### Passo 1 — Diagnosticar (somente leitura)

Roda `DBCC CHECKDB ... WITH DATA_PURITY` (ou, marcando *Verificação rápida*, `PHYSICAL_ONLY`),
`DBCC CHECKCONSTRAINTS` (chaves estrangeiras e `CHECK` — o CHECKDB não detecta registro órfão),
`DBCC CHECKCATALOG`, lê `msdb.dbo.suspect_pages`, o log de erros do SQL Server, os arquivos do
banco (inclusive se existem no servidor), o uso do log de transações, a topologia (espelhamento,
Always On, log shipping, replicação), o espaço em disco, a política de backup e configurações
como `PAGE_VERIFY`, `AUTO_SHRINK` e crescimento dos arquivos.

Com corrupção física, as checagens lógicas são adiadas para depois do reparo (lê-las sobre páginas
danificadas derrubaria a conexão). O banco não é alterado; a única escrita é limpar o histórico de
`msdb.dbo.suspect_pages` daquele banco quando o CHECKDB atual está limpo (corrupção já resolvida).

Cada diagnóstico é comparado com o anterior (histórico local) e o que é **novo** aparece em
destaque.

### Passo 2 — Resolver (em ordem de risco)

| Risco | Opção na tela | O que faz |
|---|---|---|
| Sem perda | **Acesso ao banco travado** → *Liberar acesso (voltar ao modo normal)* | Volta o banco a `MULTI_USER`; opcionalmente desconecta as sessões presas (o que não estava gravado é desfeito pelo SQL Server). |
| Sem perda | **Reconstruir índices** → *Reparar índices (sem perda de dados)* | Corrupção só em índices não clusterizados: reconstrói os índices a partir dos dados. |
| Sem perda | **Log de transações cheio** → *Resolver log cheio (sem perda de dados)* | Erro 9002: backup de log, liberação do crescimento ou arquivo de log extra em outro disco; religa o banco. Não desconecta ninguém. |
| Sem perda | **Arquivos do banco inacessíveis** → *Localizar arquivo ausente…* / *Corrigir acesso aos arquivos e religar* | Arquivo movido, sem permissão ou preso: aponta o novo caminho (`MODIFY FILE`), concede permissão à conta do SQL Server (exige Administrador no servidor) e religa. |
| Sem perda | **Reparo no modo seguro** → *Reparar (modo seguro, sem perda de dados)* | Religa (`SET ONLINE` / `RESTORE WITH RECOVERY`) e, se preciso, `DBCC CHECKDB ... REPAIR_REBUILD`. Se não bastar, para e oferece as próximas opções. *Ver script planejado* mostra os comandos sem executar. |
| Sem perda | **Ajustes recomendados** → *Aplicar os ajustes selecionados* | Configurações e metadados: `PAGE_VERIFY CHECKSUM`, desligar `AUTO_SHRINK`/`AUTO_CLOSE`, crescimento fixo, reorganizar log fragmentado (VLFs), revalidar constraints não confiáveis, reconstruir índices desabilitados, revalidar views/procedures, religar usuários órfãos, corrigir dono do banco, liberar gravação (`READ_WRITE`). Itens só informativos (compatibilidade antiga, `TRUSTWORTHY`) não têm correção automática. |
| Quarentena | **Integridade lógica: registros órfãos** → *Corrigir integridade (órfãos para a quarentena)* | Tenta recuperar o registro-pai de um backup; os órfãos que sobram saem da tabela e ficam no banco `RestoreDB_Quarentena`. |
| Restaura backup | **Restaurar de backup** | *Restaurar cadeia completa (mais recente)* (FULL → diferencial → logs, com tail-log), *Restaurar o backup selecionado* e, em *Mais opções de restauração (avançado)*: testar backup, `VERIFYONLY`, recuperar para cópia, ponto no tempo (`STOPAT`), `STANDBY`, restaurar só as páginas afetadas. |
| Sem perda | **Assistente de salvamento** → *Copiar linhas faltantes desta tabela* | De uma cópia recuperada para o banco vivo, só as linhas que faltam (por chave primária). |
| Pode perder dados | **Reparo com possível perda de dados** → *Executar reparo COM perda de dados* | `REPAIR_ALLOW_DATA_LOSS`, depois de *Simular antes*, com tail-log, backup de segurança, salvamento das linhas legíveis e backup FULL ao final. |
| Último recurso | **Reconstruir o log de transações** → *Reconstruir log (recriar .ldf)* | Desanexa, guarda o `.ldf` antigo e recria o log (`FOR ATTACH_REBUILD_LOG`). Exige Administrador no próprio servidor. |

### Ferramentas adicionais

- **Verificar uma tabela**: `DBCC CHECKTABLE` de uma tabela e *Reajustar identidades*
  (`DBCC CHECKIDENT ... RESEED`).
- **Manutenção preventiva**: *Otimizar* (índices `REORGANIZE`/`REBUILD` + `sp_updatestats`),
  *Corrigir contadores* (`DBCC UPDATEUSAGE`) e *Gerar comando* para a verificação agendada.
- **Verificação em partes**: `DBCC CHECKTABLE` tabela a tabela por um tempo limitado (15 min a 2 h),
  lembrando onde parou; no início de cada ciclo, `CHECKALLOC` e `CHECKCATALOG`.
- **Recuperar exclusão acidental**: lê o log de transações e lista `DELETE`, `DROP` e `TRUNCATE`
  recentes; devolve só as linhas excluídas (sem sobrescrever o banco) ou restaura o banco inteiro
  até antes da transação.

Fora dos passos, o cabeçalho tem **📎 Anexar .mdf** (anexa um banco a partir de um arquivo de dados
solto, recriando o log se o `.ldf` não existir) e o registro tem **Exportar relatório** (HTML).

## Requisitos

| Item | Requisito |
|---|---|
| Sistema | Windows 10 ou 11 (x86 ou x64) — o app é WPF, só roda no Windows. |
| Runtime | Nenhum para o executável publicado (é *self-contained*). Para rodar a partir do código: **.NET 10 Desktop Runtime**; para compilar: **.NET 10 SDK**. |
| SQL Server | **2008 R2 a 2025** (validado em 2008 R2 SP3 Express e 2025 Developer). Azure SQL Database não é suportado (o app avisa). |
| Login | **sysadmin** na instância. Sem ele, o app avisa ao conectar: `DBCC ... REPAIR`, `ALTER DATABASE` e `RESTORE` vão falhar. |
| Opções locais | *Corrigir acesso aos arquivos e religar* (permissão em pasta) e *Reconstruir log (recriar .ldf)* exigem **executar o app como Administrador no próprio servidor** do SQL Server. Sem isso, nada é alterado e os comandos aparecem no registro. |

## Baixar, compilar e publicar

```powershell
git clone https://github.com/npvitor/RestoreDB.git
cd RestoreDB

# Compilar e rodar (precisa do .NET 10 SDK)
dotnet build RestoreDB.slnx -c Release
dotnet run --project src/RestoreDB.App -c Release
```

**Publicar** (gera o executável para levar ao cliente):

```powershell
dotnet publish src/RestoreDB.App/RestoreDB.App.csproj -p:PublishProfile=FolderProfile
```

O perfil [`FolderProfile.pubxml`](src/RestoreDB.App/Properties/PublishProfiles/FolderProfile.pubxml)
gera um executável **win-x86, self-contained, arquivo único** em
`src/RestoreDB.App/bin/Release/net10.0-windows/publish/win-x86/`. Copie **a pasta inteira**: além
do `RestoreDB.App.exe` ficam ao lado algumas DLLs nativas do WPF e do SqlClient que o executável usa.
O x86 roda tanto em Windows 32 quanto 64 bits.

**Autoteste da interface** (sem conexão; valida XAML e os dois temas):

```powershell
.\RestoreDB.App.exe --xaml-selftest     # imprime SELFTEST-OK e sai com código 0
```

**Manual em PDF**: `pwsh docs/build-manual-pdf.ps1` gera `docs/MANUAL.pdf` a partir do
`docs/MANUAL.md` (PowerShell 7 + Chrome ou Edge).

## Segurança e limites

**O que a ferramenta garante**

- Opera sempre a partir de `master` e numa única sessão por operação. Nomes de banco entram com
  `QUOTENAME` e caminhos como parâmetro (`sp_executesql`) — sem concatenação insegura.
- As opções que podem perder dados (reparo com perda, reconstrução do log, remoção de órfãos,
  restaurações que substituem o banco) ficam bloqueadas até a confirmação explícita.
- Bancos de sistema (`master`, `model`, `msdb`, `tempdb`) nunca recebem reparo destrutivo nem
  restauração; o app no máximo tenta religá-los e mostra um procedimento manual.
- Com espelhamento, Always On, log shipping ou replicação, os reparos (inclusive índices, órfãos e
  reconstrução do log) são bloqueados: o app pede para tirar o banco dessa configuração antes.
- Antes do reparo com perda: tail-log (`BACKUP LOG ... NO_TRUNCATE`), backup `COPY_ONLY`, cópia dos
  arquivos quando locais, e cópia das linhas ainda legíveis das tabelas danificadas.
- Senhas não são gravadas em disco; a conexão é refeita a cada abertura do app.

**Limites — leia antes de usar em produção**

- **O reparo com perda pode descartar dados.** `REPAIR_ALLOW_DATA_LOSS` desaloca o que está
  corrompido. A ferramenta devolve o que conseguir (backup que cubra o instante do reparo e as linhas
  que ainda eram legíveis), mas **o que estava em páginas destruídas e não está em nenhum backup
  se perde**. Quando há backup íntegro, restaurar costuma ser melhor.
- **O salvamento sem backup não recupera páginas destruídas.** Ele copia só o que ainda dá para
  ler; de linhas em páginas ilegíveis, no máximo uma versão parcial (só as colunas de um índice) fica
  guardada para revisão (`tabela__parcial`), sem voltar sozinha.
- **Restaurar substitui o banco.** O que foi gravado depois do backup (ou do ponto no tempo
  escolhido) se perde, a não ser que o tail-log possa ser salvo — e ele não é salvo no modelo
  `SIMPLE`, com o log danificado, em *Restaurar o backup selecionado* nem em `STANDBY`.
- **Reconstruir o log pode deixar o banco inconsistente**: transações em andamento se perdem.
- **Remoção de órfãos**: se a quarentena não puder ser usada numa tabela, as linhas são excluídas
  (o registro avisa; há backup de segurança).
- A conexão usa `Encrypt=Optional` e `TrustServerCertificate=true` para funcionar com o 2008 R2
  (TLS antigo): **use numa rede confiável**.
- A ferramenta cria arquivos e bancos auxiliares que **não apaga sozinha quando ficam para
  revisão** — veja abaixo. Eles ocupam espaço no servidor.

**Arquivos e bancos criados**

| Onde | O quê |
|---|---|
| `%LOCALAPPDATA%\RestoreDB\Logs\repair-AAAAMMDD.log` | Registro de cada comando executado. |
| `%LOCALAPPDATA%\RestoreDB\History\<banco>.json` | Histórico dos diagnósticos (para apontar o que é novo). |
| `%LOCALAPPDATA%\RestoreDB\IncrementalCheck\<banco>.json` | Progresso da verificação em partes. |
| `%LOCALAPPDATA%\RestoreDB\Alerts\` | Alertas do monitoramento agendado. |
| Pasta padrão de backups do SQL Server | `<banco>_tail_<data>.trn` (tail-log, antes de reparos destrutivos e de restaurações), `<banco>_RestoreDB_<data>.bak` (backup de segurança `COPY_ONLY` antes do reparo destrutivo) e `<banco>_PosReparo_<data>.bak` (backup FULL depois de um reparo com perda bem-sucedido). |
| Bancos na mesma instância | `RestoreDB_Quarentena` (órfãos removidos), `<banco>_Recuperado` (recuperar para cópia), `<banco>_Salvamento_<data>` (linhas legíveis guardadas antes do reparo, quando nem todas voltaram), `<banco>_AntesExclusao_<data>` (quando nem todas as linhas excluídas puderam voltar). Bancos temporários de teste e simulação são apagados ao final. |
| Pasta do `.ldf` | `<arquivo>.corrupt_AAAAMMDDHHMMSS` — o log antigo guardado pela reconstrução do log. |

## Monitoramento agendado (RestoreDB.Probe)

[`tools/RestoreDB.Probe`](tools/RestoreDB.Probe) é um utilitário de linha de comando que chama os
mesmos serviços do app. O uso principal é a **verificação diária**: `monitor <banco|all>` roda o
diagnóstico (somente leitura), compara com a verificação anterior e, havendo novidade (corrupção
nova, mais violações, mais páginas suspeitas, banco fora do ar), grava um alerta em
`%LOCALAPPDATA%\RestoreDB\Alerts` e **sai com código 2** (0 = nada novo). A primeira execução só
registra a linha de base — ela não alerta.

```powershell
# Publicar o Probe (não faz parte da solução nem do perfil de publicação do app)
dotnet publish tools/RestoreDB.Probe -c Release -r win-x86 --self-contained -p:PublishSingleFile=true -o publish\Probe

# Conexão: variáveis de ambiente
$env:PROBE_SERVER = "MEUSERVIDOR\INSTANCIA"   # padrão: localhost
# sem PROBE_USER = autenticação do Windows; com ele, autenticação SQL:
# $env:PROBE_USER = "usuario"; $env:PROBE_PASS = "..."

.\publish\Probe\RestoreDB.Probe.exe monitor MeuBanco
```

No app, **Ferramentas adicionais → Manutenção preventiva → Verificação agendada → Gerar comando**
mostra o `schtasks` pronto para o banco selecionado (com autenticação do Windows: a conta da tarefa
precisa ser sysadmin). Outros comandos do Probe: `diagnose`, `repair`, `repair-indexes`,
`fix-constraints`, `simulate-repair`, `maintain`, `fragmentation`, `forensic`, `attach`, `restore`,
`restore-pit`, `restore-pages`, `restore-standby`, `finalize-recovery`, `test-restore`,
`restore-copy`, `salvage-table`, `verify-backups`, `check-table`, `reseed`, `history`,
`rebuild-log`, `resolve-log`, `fix-files`, `recover-deleted`, `state` (veja o cabeçalho do
[`Program.cs`](tools/RestoreDB.Probe/Program.cs)). Vários deles alteram o banco — use com o mesmo
cuidado da interface.

## Testes

**Unitários** — [`tests/RestoreDB.Tests`](tests/RestoreDB.Tests) (xUnit, sem SQL Server):

```powershell
dotnet test tests/RestoreDB.Tests -c Release
```

**Integração** — [`tests/RestoreDB.IntegrationTests`](tests/RestoreDB.IntegrationTests): rodam os
serviços reais contra uma instância SQL, inclusive com **corrupção física real** (`DBCC WRITEPAGE`).
Cada teste cria e apaga seu próprio banco `RestoreDB_IT_*`. Sem as variáveis abaixo, são ignorados.

> ⚠️ **Nunca aponte os testes de integração para um servidor de produção.** Eles corrompem bancos,
> colocam bancos em `SUSPECT`, criam logins e apagam backups gerados.

```powershell
$env:RESTOREDB_IT_SERVER = "localhost"        # instância de TESTE
$env:RESTOREDB_IT_USE_WINDOWS = "1"           # ou RESTOREDB_IT_USER / RESTOREDB_IT_PASS
dotnet test tests/RestoreDB.IntegrationTests -c Release
```

Alguns cenários dependem do motor: transações adiadas (Msg 7929) aparecem na edição
Developer/Enterprise; a reconstrução do log sem Administrador é testada pelo caminho em que ela
aborta sem tocar no banco.

`sql/test-setup.sql` cria um banco de exemplo e estados problemáticos para testes manuais;
`sql/repair-scripts.sql` mostra, para revisão por um DBA, os comandos principais do fluxo de reparo.

## Estrutura do projeto

```
RestoreDB.slnx
src/
  RestoreDB.App/          Interface WPF (ConnectionWindow, MainWindow, temas claro/escuro, ícone)
  RestoreDB.Services/     Regras: diagnóstico, reparo, restauração, ajustes, salvamento, manutenção,
                          monitoramento, histórico, relatório
  RestoreDB.DataAccess/   ADO.NET (Microsoft.Data.SqlClient): sessão, connection string, consultas
  RestoreDB.Domain/       Modelos e AppConstants (parâmetros fixos da conexão, estados, nomes)
tests/
  RestoreDB.Tests/              Unitários (sem banco)
  RestoreDB.IntegrationTests/   Integração (instância SQL de teste)
tools/
  RestoreDB.Probe/        Linha de comando: monitoramento agendado e operações sem interface
sql/                      Script de cenários de teste e referência dos comandos de reparo
docs/                     Manual do usuário (Markdown + PDF) e script que gera o PDF
```

Parâmetros fixos (catálogo `master`, timeout de conexão de 15 s, timeout de consulta de 30 s,
comandos de reparo sem timeout, pool, nome da aplicação na conexão, estados considerados
problemáticos, bancos de sistema) ficam em
[`AppConstants.cs`](src/RestoreDB.Domain/Constants/AppConstants.cs).

## Licença

[MIT](LICENSE) — uso, modificação e redistribuição livres, inclusive comercial, mantendo o aviso de
copyright. O software é fornecido **sem garantia** (*"AS IS"*): leia [Segurança e limites](#segurança-e-limites)
antes de usá-lo em bancos de produção.
