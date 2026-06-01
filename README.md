# RestoreDB — Diagnóstico e Reparo de Bancos SQL Server

Ferramenta desktop (WPF / .NET 10) para **diagnosticar e reparar** bancos de dados
SQL Server que estejam em estados problemáticos: `SUSPECT`, `RECOVERY_PENDING`,
`EMERGENCY`, `RECOVERING`, `RESTORING` e `OFFLINE`. Voltada ao **Módulo de
Faturamento**, com linguagem acessível a usuários leigos.

Todo o T-SQL é **compatível com SQL Server 2008 R2** (e versões superiores).

> 📘 **Novo por aqui?** Comece pelo **Manual de Utilização** — guia passo a passo, em
> linguagem acessível, para operar a ferramenta: [Markdown](docs/MANUAL.md) · [PDF](docs/MANUAL.pdf).

**Recursos principais:**

- **Diagnóstico autoritativo** (somente leitura): `msdb.dbo.suspect_pages`,
  `DBCC CHECKDB ... WITH TABLERESULTS` (estruturado), log de erros, topologia,
  espaço em disco e `PAGE_VERIFY`.
- **Escalonamento de reparo** do menos ao mais agressivo, **sempre primeiro SEM perda de dados**.
- **Reparo SEM perda quando a corrupção está só em índices não-clusterizados**
  (reconstrói os índices em vez de descartar dados).
- **Restaurar de backup** (full/diferencial/log), incluindo **restauração de página**
  e correção de usuários órfãos — o caminho correto para corrupção de dados.
- **Guarda de topologia**: bloqueia reparo destrutivo em bancos espelhados, em Always On,
  log shipping ou replicação.
- **Reconstrução do log de transações** como último recurso.
- **Autenticação SQL ou Windows**, **pré-checagem de sysadmin/edição**, **botão Cancelar**,
  **exportação de relatório (HTML)** e **pré-visualização do script (dry-run)**.

---

## Arquitetura (em camadas)

Fluxo de dependência: **Tela → Code-behind → Service → Data Access**.

```
src/
├─ RestoreDB.Domain/        Modelos, constantes e enums (sem dependências)
│   ├─ Constants/AppConstants.cs        Parâmetros fixos (connection string, estados, etc.)
│   └─ Models/                          ConnectionInfo, DatabaseInfo, RepairResult, logs...
│
├─ RestoreDB.DataAccess/    ADO.NET puro (Microsoft.Data.SqlClient)
│   ├─ SqlDataAccess.cs                 Connection string, teste, listagem, abertura de sessão
│   └─ SqlSession.cs                    Uma conexão mantida durante toda a sequência de reparo
│
├─ RestoreDB.Services/      Lógica + validações
│   ├─ ConnectionService.cs             Validação e tradução de erros de conexão
│   ├─ ServerService.cs                 Capacidades da instância + pré-checagem (sysadmin/edição)
│   ├─ DatabaseService.cs               Listagem de bancos
│   ├─ RepairService.cs                 Orquestração do reparo + diagnóstico (escalonamento)
│   ├─ RestoreService.cs                Restauração de backup (full/diff/log, página, usuários órfãos)
│   ├─ ReportService.cs                 Exportação do relatório da sessão (HTML)
│   └─ LogService.cs                    Log em arquivo (timestamp, thread-safe)
│
└─ RestoreDB.App/           WPF (View + code-behind, SEM MVVM)
    ├─ App.xaml                         Estilos/recursos globais
    ├─ Views/ConnectionWindow.xaml      Tela de conexão (SQL ou Windows auth)
    ├─ Views/MainWindow.xaml            Listagem + diagnóstico + reparo + restore + log ao vivo
    └─ Converters/                      Cores do log por nível

tests/
└─ RestoreDB.Tests/         xUnit — testes de lógica pura (sem banco)
```

- **ADO.NET puro** (sem ORM).
- **Sem padrão MVVM**: a regra fica nos *Services*; a *View* apenas chama serviços
  pelo code-behind.

---

## Como compilar e executar

Pré-requisito: **.NET 10 SDK** (Windows).

```powershell
dotnet build RestoreDB.slnx
dotnet run --project src/RestoreDB.App
dotnet test RestoreDB.slnx          # testes de lógica (xUnit)
```

---

## Fluxo de uso

1. **Conexão** — informe *Servidor* e, no modo SQL, *Usuário* e *Senha*; ou marque
   **"Usar autenticação do Windows"**. Os demais parâmetros (timeout, criptografia,
   *application name*, catálogo `master`, pooling) são **constantes** em `AppConstants`.
   Ao conectar, a ferramenta faz uma **pré-checagem**: avisa se o login **não é sysadmin**
   ou se a instância é **Azure SQL Database** (onde o reparo local não se aplica).
2. **Listagem** — todos os bancos e seus `state_desc` (de `sys.databases`).
   Os bancos problemáticos ficam **destacados em vermelho**.
3. **Diagnóstico (1ª etapa)** — ao selecionar um banco problemático, a **única**
   ação inicial é **"Diagnosticar (sem alterar o banco)"**. Só **depois** que o
   diagnóstico roda e recomenda é que as **opções de reparo** aparecem (e, quando o
   log está corrompido, a opção de reconstrução já vem destacada).
4. **Reparo** — a ferramenta oferece **primeiro o reparo SEM perda de dados**.
   Apenas se ele falhar, oferece o reparo **COM possível perda de dados**, com aviso
   destacado e exigindo **confirmação explícita** (checkbox + diálogo). O estado é
   atualizado na lista após cada operação, e cada comando é registrado no log
   (tela + arquivo).

> O login utilizado precisa ser **sysadmin** (DBCC CHECKDB / ALTER DATABASE).

---

## Diagnóstico — somente leitura

Para qualquer banco problemático há o botão **"Diagnosticar (sem alterar o banco)"**.
Sem modificar nada, ele cruza **fontes autoritativas** e recomenda o melhor caminho:

- **`msdb.dbo.suspect_pages`** — lista oficial de páginas corrompidas (erros 823/824/825);
- **`DBCC CHECKDB ... WITH TABLERESULTS`** (estruturado) — identifica **objeto e índice**
  afetados e o **nível mínimo de reparo**; resolve nomes de tabela/índice para leitura;
- **log de erros** (`sp_readerrorlog`, vários arquivos) — discrimina **DADOS** (`.mdf`/`.ndf`)
  × **LOG** (`.ldf`) pelo nome físico do arquivo (independe de idioma) e detecta **9002**
  (log cheio) e **823/824/825** (E/S);
- **topologia** (espelhamento/Always On/log shipping/replicação);
- **espaço em disco** dos volumes do banco (`sys.dm_os_volume_stats`);
- **`PAGE_VERIFY`** — recomenda `CHECKSUM` quando estiver `NONE`/`TORN_PAGE_DETECTION`.

A conclusão define a **ação recomendada** e a UI destaca o caminho certo:

- corrupção **só em índices não-clusterizados** ⇒ **reparo SEM perda** (reconstruir índices);
- corrupção de **dados** ⇒ **restaurar de backup** (ou **restauração de página**, se poucas);
- corrupção de **log** ⇒ **reconstruir o log**;
- **log cheio (9002)** ⇒ liberar espaço / `BACKUP LOG` (não é corrupção);
- **topologia de alta disponibilidade** ⇒ alerta para tratar antes de reparar.

## Lógica de reparo (escalonamento: do menos ao mais agressivo)

| Estado            | Ação                                                                              | Perda de dados |
|-------------------|-----------------------------------------------------------------------------------|----------------|
| `OFFLINE`         | `SET ONLINE` → `SET MULTI_USER`                                                    | Não            |
| `RESTORING`/`RECOVERING` | `RESTORE DATABASE ... WITH RECOVERY`                                        | Não            |
| `RECOVERY_PENDING`| Tenta `SET ONLINE`; se falhar, cai no fluxo de emergência abaixo                   | Não → (talvez) |
| `SUSPECT`/`EMERGENCY` | `SET EMERGENCY` → `SET SINGLE_USER WITH ROLLBACK IMMEDIATE` → `DBCC CHECKDB` (diagnóstico) → `SET ONLINE` (recuperação) → `REPAIR_REBUILD` → verificação | Não |
| ↳ se restar só corrupção de **índice não-clusterizado** | `ALTER INDEX ... REBUILD` (reconstrói os índices afetados) → verificação | **Não** |
| ↳ se o reparo seguro não bastar | **(com confirmação)** `EMERGENCY` + `SINGLE_USER` → backup `COPY_ONLY` + **cópia física** dos arquivos → `REPAIR_ALLOW_DATA_LOSS` → verificação → `MULTI_USER` | **Sim** |
| **Corrupção de dados** (recomendado) | **Restaurar de backup** (`RESTORE`/`RESTORE ... PAGE`) → corrigir usuários órfãos | Não* |

> **Detalhe validado em engine real:** o SQL Server rejeita `REPAIR_REBUILD` com o
> banco em modo `EMERGENCY` (Msg 7901). Por isso o fluxo tenta a **recuperação
> normal (`SET ONLINE`) primeiro**; com o banco ONLINE + `SINGLE_USER`, o
> `REPAIR_REBUILD` (sem perda de dados) passa a ser permitido. O
> `REPAIR_ALLOW_DATA_LOSS`, ao contrário, **é** permitido em `EMERGENCY`.

### Último recurso: reconstruir o log de transações (recriar o `.ldf`)

Quando o reparo é **impossível** (resultado *Failed*), a ferramenta oferece — para
bancos de usuário, com **confirmação explícita** — a reconstrução do log:

1. `SET EMERGENCY` → `sp_detach_db` (desanexa e libera os arquivos);
2. **remove** o arquivo de log antigo (renomeia para `*.corrupt_AAAAMMDDHHMMSS`);
3. `CREATE DATABASE [x] ON (FILENAME='...mdf') FOR ATTACH_REBUILD_LOG` (cria um log novo);
4. `SET MULTI_USER` + `DBCC CHECKDB` (verificação).

> **Validado em engine real:** `sp_detach_db` funciona com o banco em `EMERGENCY`,
> e `ATTACH_REBUILD_LOG` **só recria o log se o `.ldf` não existir fisicamente** —
> daí a remoção do arquivo ser obrigatória. **Pode haver perda de dados.**
>
> Requisitos: os arquivos do banco precisam estar **acessíveis a partir da máquina
> que roda a ferramenta** (servidor local) e a aplicação precisa de permissão na
> pasta de dados. **Execute a ferramenta como Administrador** — para remover o `.ldf`
> do SQL Server ela precisa de privilégios (tenta inclusive *assumir a posse* do
> arquivo via `takeown`/`icacls`). A partir da correção, isso é **verificado ANTES
> de desanexar** o banco: sem elevação/permissão, a operação é **abortada sem tocar
> no banco**. Se algo falhar após o detach, a ferramenta tenta **reverter** (reanexar
> o log original) e, em último caso, registra o comando manual de recuperação.
>
> **Alternativa sem Administrador:** quando o problema é o log, o **reparo COM perda
> de dados** (`DBCC CHECKDB ... REPAIR_ALLOW_DATA_LOSS` em modo `EMERGENCY`) também
> reconstrói o log — porém **inteiramente no servidor**, sem mexer em arquivos, logo
> **não exige Administrador**. Prefira esse caminho quando possível.

Detalhes importantes:

- Toda a sequência roda na **mesma conexão** (`SqlSession`), garantindo que o
  bloqueio `SINGLE_USER` seja detido por quem executa o `DBCC CHECKDB`.
- A aplicação conecta-se sempre em **`master`** — nunca diretamente no banco
  quebrado.
- Antes do reparo destrutivo, tenta-se **`BACKUP DATABASE ... WITH COPY_ONLY`**;
  se não for possível (banco inacessível), o usuário é avisado.
- Ao final de qualquer caminho, o banco volta para **`MULTI_USER`**.
- Bancos de **sistema** (`master`, `model`, `msdb`, `tempdb`) **não** são
  submetidos a reparo destrutivo automático.

### Reparo SEM perda de dados: índices não-clusterizados

Boa parte das "corrupções" afeta apenas **índices não-clusterizados** (`index_id > 1`),
que são **redundantes**. Quando o `DBCC CHECKDB ... WITH TABLERESULTS` confirma que **toda**
a corrupção está nesses índices, a ferramenta os **reconstrói** (`ALTER INDEX ... REBUILD`)
— **sem perder nenhum dado** — em vez de partir para o `REPAIR_ALLOW_DATA_LOSS`. O fluxo de
emergência faz isso automaticamente antes de escalar, e o botão **"Reparar índices (sem perda
de dados)"** fica disponível quando o diagnóstico identifica esse caso.

### Restaurar de backup (corrupção de dados)

Para corrupção nos **dados** (`.mdf`/`.ndf`), o caminho correto é **restaurar de backup** —
`REPAIR_ALLOW_DATA_LOSS` apenas *descarta* o que está corrompido. O painel **"Restaurar de
backup"**:

- lista os backups do **histórico** (`msdb.dbo.backupset`) ou de um **arquivo `.bak`** escolhido
  (`RESTORE HEADERONLY`);
- **verifica** a mídia (`RESTORE VERIFYONLY`) antes de aplicar;
- executa `RESTORE DATABASE ... WITH REPLACE` (e `MOVE`/`STOPAT` quando aplicável);
- ao final, **corrige usuários órfãos** (reassocia ao login de mesmo nome).

Também há **restauração de página** (`RESTORE ... PAGE`) para recuperar apenas as páginas
corrompidas, preservando o restante (requer modelo de recuperação FULL e a cadeia de backups).

### Segurança de topologia

Antes de qualquer operação destrutiva (`SET EMERGENCY`/`SINGLE_USER`, detach, `RESTORE`),
a ferramenta detecta **espelhamento**, **Always On (AG)**, **log shipping** e **replicação**.
Se o banco participar de alguma dessas configurações, o reparo destrutivo é **bloqueado** com
orientação (fazer *failover*, remover do AG/espelhamento, desativar log shipping/replicação),
evitando quebrar a topologia. Bancos com **FILESTREAM/full-text** também não entram na
reconstrução de log por detach/attach.

### Pré-checagens, cancelamento e relatório

- **Pré-checagem na conexão**: avisa se o login **não é sysadmin** ou se é **Azure SQL Database**.
- **Botão Cancelar**: interrompe a operação em andamento (propaga `CancellationToken`).
- **Ver script planejado (dry-run)**: mostra o T-SQL que *seria* executado, sem executar nada
  — útil para revisão por DBA ou execução manual em servidor remoto.
- **Exportar relatório (HTML)**: salva o registro da sessão para anexar a um chamado.

Os comandos exatos estão em [`sql/repair-scripts.sql`](sql/repair-scripts.sql).
Cenários de teste seguros em [`sql/test-setup.sql`](sql/test-setup.sql).

---

## Segurança e robustez

- **Sem injeção de SQL**: o nome do banco é protegido com `QUOTENAME` e passado
  como **parâmetro** a `sp_executesql` (nunca concatenação direta de entrada).
- **UI sempre responsiva**: operações longas usam `async/await`; o progresso é
  reportado via `IProgress<RepairLogEntry>`.
- **Sem stack trace cru na tela**: exceções viram mensagens amigáveis; há ainda
  um *handler* global de último recurso.
- **Log com timestamp** de todas as ações em
  `%LOCALAPPDATA%\RestoreDB\Logs\repair-AAAAMMDD.log` e na própria tela.

---

## Conexão (SQL Server 2008 R2)

`Microsoft.Data.SqlClient` é configurado com `Encrypt=Optional`
(`SqlConnectionEncryptOption.Optional`) e `TrustServerCertificate=True` para
compatibilidade com o TLS de servidores antigos (2008 R2). É possível usar
**autenticação SQL** (Usuário/Senha) ou **autenticação do Windows**
(`Integrated Security`), escolhida na tela de conexão.

---

## Testes

**Testes de lógica** ([`tests/RestoreDB.Tests`](tests/RestoreDB.Tests), xUnit) — não precisam de
SQL Server: *connection string* (SQL × Windows auth), script planejado (*dry-run*), classificação
de corrupção só-índice (`CheckDbReport`), topologia/disco e utilitários de domínio.

```powershell
dotnet test RestoreDB.slnx
```

**Testes de integração** ([`tests/RestoreDB.IntegrationTests`](tests/RestoreDB.IntegrationTests)) —
exercitam os serviços reais contra uma instância SQL: conexão/`ServerInfo`, reparo `OFFLINE`,
`EMERGENCY` e `RESTORING`, diagnóstico estruturado (CHECKDB/`suspect_pages`/topologia/disco),
restauração de backup (revertendo perda de dados) e `HEADERONLY`/`FILELISTONLY`. Cada teste cria
e **derruba** seu próprio banco `RestoreDB_IT_*` (isolado) e limpa os backups gerados. Sem as
variáveis de ambiente abaixo, esses testes são **ignorados** (o `dotnet test` padrão segue verde).

```powershell
# Autenticação SQL:
$env:RESTOREDB_IT_SERVER = "localhost"
$env:RESTOREDB_IT_USER   = "sa"
$env:RESTOREDB_IT_PASS   = "********"
# ...ou autenticação do Windows (sem senha):
$env:RESTOREDB_IT_SERVER       = "localhost"
$env:RESTOREDB_IT_USE_WINDOWS  = "1"
dotnet test tests/RestoreDB.IntegrationTests
```

> Validado contra **SQL Server 2022 (Developer Edition)**: 41 testes de lógica + 10 de integração,
> todos aprovados.
>
> **Também validado em produção contra SQL Server 2008 R2 SP3 (Express Edition, 10.50.6000.34)**
> com **autenticação do Windows**: 41 testes de lógica + 10 de integração aprovados, e os fluxos de
> reparo exercitados com **corrupção física real** (`DBCC WRITEPAGE`) sobre uma cópia de um banco de
> produção (≈900 MB) — ver abaixo. O `Microsoft.Data.SqlClient` conecta-se ao 2008 R2 negociando
> TLS 1.0 (emite apenas um **aviso** benigno; `Encrypt=Optional` torna a conexão possível).

### Validação com corrupção real

Os caminhos destrutivos foram exercitados contra **corrupção física real** (via `DBCC WRITEPAGE`)
num banco de produção de teste, com backup prévio e restauração ao final:

- **Índice não-clusterizado corrompido** ⇒ a ferramenta detecta "só índice", reconstrói
  (`DISABLE` + `REBUILD`) e verifica — **zero perda de dados** (checksum idêntico ao original).
- **Página de dados corrompida** + `EMERGENCY` ⇒ `REPAIR_ALLOW_DATA_LOSS` torna o banco
  consistente (perda mínima, registrada).
- **Restaurar de backup** ⇒ recupera 100% dos dados; corrige usuários órfãos.
- O diagnóstico **reconcilia histórico × estado atual**: entradas antigas de `msdb.suspect_pages`
  e do log de erros não geram falso positivo quando o `DBCC CHECKDB` ao vivo está limpo.
- **Reconstrução de log (último recurso)** sem privilégio de Administrador ⇒ a ferramenta **aborta
  ANTES de desanexar** (pré-checagem de elevação), deixa o banco **intacto** e imprime as instruções
  de recuperação manual — confirmando a garantia de "não piorar o estado".
- Estados `OFFLINE` e `RESTORING` ⇒ religados sem perda (`SET ONLINE` / `RESTORE WITH RECOVERY`).

O utilitário [`tools/RestoreDB.Probe`](tools/RestoreDB.Probe) é um runner de linha de comando que
invoca os serviços reais (`diagnose`/`repair-indexes`/`repair`/`rebuild-log`/`restore`) — usado nessa
validação e útil para operar a ferramenta sem a interface gráfica.
