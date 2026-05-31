# RestoreDB — Diagnóstico e Reparo de Bancos SQL Server

Ferramenta desktop (WPF / .NET 10) para **diagnosticar e reparar** bancos de dados
SQL Server que estejam em estados problemáticos: `SUSPECT`, `RECOVERY_PENDING`,
`EMERGENCY`, `RECOVERING`, `RESTORING` e `OFFLINE`. Voltada ao **Módulo de
Faturamento**, com linguagem acessível a usuários leigos.

Todo o T-SQL é **compatível com SQL Server 2008 R2** (e versões superiores).

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
│   ├─ DatabaseService.cs               Listagem de bancos
│   ├─ RepairService.cs                 Orquestração do reparo (escalonamento)
│   └─ LogService.cs                    Log em arquivo (timestamp, thread-safe)
│
└─ RestoreDB.App/           WPF (View + code-behind, SEM MVVM)
    ├─ App.xaml                         Estilos/recursos globais
    ├─ Views/ConnectionWindow.xaml      Tela de conexão
    ├─ Views/MainWindow.xaml            Listagem + reparo + log ao vivo
    └─ Converters/                      Cores do log por nível
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
```

---

## Fluxo de uso

1. **Conexão** — informe *Servidor*, *Usuário* e *Senha*. Os demais parâmetros
   (timeout, criptografia, *application name*, catálogo `master`, pooling) são
   **constantes** em `AppConstants`. O botão **Conectar** valida e segue.
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

## Diagnóstico (DADOS × LOG) — somente leitura

Para qualquer banco problemático há o botão **"Diagnosticar (sem alterar o banco)"**.
Ele lê o **log de erros do SQL Server** (`sp_readerrorlog`) e os arquivos de
`sys.master_files` e conclui, **sem modificar nada**, se a corrupção está nos
arquivos de **DADOS** (`.mdf`/`.ndf`) ou no **LOG de transações** (`.ldf`):

- erros de E/S / checksum / *file activation* referenciando o `.ldf` ⇒ **log corrompido**
  (os dados tendem a estar íntegros) ⇒ recomenda **reconstruir o log**;
- erros referenciando `.mdf`/`.ndf` ⇒ **dados corrompidos** ⇒ recomenda **restaurar de backup**.

A detecção é tolerante a idioma (palavras-chave EN/PT) e usa o **nome físico do arquivo**
como discriminador (independe de idioma). Quando conclui que o log está corrompido, a UI
já destaca a opção de reconstrução.

## Lógica de reparo (escalonamento: do menos ao mais agressivo)

| Estado            | Ação                                                                              | Perda de dados |
|-------------------|-----------------------------------------------------------------------------------|----------------|
| `OFFLINE`         | `SET ONLINE` → `SET MULTI_USER`                                                    | Não            |
| `RESTORING`/`RECOVERING` | `RESTORE DATABASE ... WITH RECOVERY`                                        | Não            |
| `RECOVERY_PENDING`| Tenta `SET ONLINE`; se falhar, cai no fluxo de emergência abaixo                   | Não → (talvez) |
| `SUSPECT`/`EMERGENCY` | `SET EMERGENCY` → `SET SINGLE_USER WITH ROLLBACK IMMEDIATE` → `DBCC CHECKDB` (diagnóstico) → `SET ONLINE` (recuperação) → `REPAIR_REBUILD` → verificação | Não |
| ↳ se o reparo seguro não bastar | **(com confirmação)** `EMERGENCY` + `SINGLE_USER` → backup `COPY_ONLY` → `REPAIR_ALLOW_DATA_LOSS` → verificação → `MULTI_USER` | **Sim** |

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
compatibilidade com o TLS de servidores antigos (2008 R2).
