# RestoreDB — Diagnóstico e Reparo de Bancos SQL Server

Ferramenta desktop (WPF / .NET 10) para **diagnosticar e reparar** bancos de dados
SQL Server que estejam em estados problemáticos: `SUSPECT`, `RECOVERY_PENDING`,
`EMERGENCY`, `RECOVERING`, `RESTORING` e `OFFLINE`. Voltada ao **Módulo de
Faturamento**, com linguagem acessível a usuários leigos.

Todo o T-SQL é **compatível com SQL Server 2008 R2** (e versões superiores).

> 📘 **Novo por aqui?** Comece pelo **Manual de Utilização** — guia passo a passo, em
> linguagem acessível, para operar a ferramenta: [Markdown](docs/MANUAL.md) · [PDF](docs/MANUAL.pdf).

> ⚠️ **Importante:** um banco pode estar **`ONLINE` e aparentemente saudável**, mas conter
> corrupção física (que só o `DBCC CHECKDB` revela) ou **violações de integridade lógica** —
> p.ex. uma chave estrangeira apontando para um registro inexistente (registro órfão), que o
> CHECKDB **não** detecta. Por isso o **Diagnóstico** está disponível para **qualquer banco**,
> não apenas os que estão em estado problemático, e a elegibilidade de reparo passa a depender do
> **resultado do diagnóstico**, não do `state_desc`.

**Recursos principais:**

- **Diagnóstico autoritativo** (somente leitura) e **abrangente**: `DBCC CHECKDB ... WITH
  TABLERESULTS` (consistência física, estruturada), **`DBCC CHECKCONSTRAINTS`** (integridade
  referencial — FK/CHECK), **`DBCC CHECKCATALOG`** (metadados), constraints **não confiáveis**
  (`is_not_trusted`) e **desabilitadas** (respeitadas, nunca removidas), `msdb.dbo.suspect_pages`, log de
  erros, **arquivos do banco** (ausente/sem permissão/preso), **uso do log de transações**, topologia,
  espaço em disco e `PAGE_VERIFY`.
- **Diagnóstico de qualquer banco** — inclusive `ONLINE` — sem precisar forçar `EMERGENCY` na mão.
- **Correção de integridade lógica com recuperação do registro-pai a partir de backup**: antes de
  **remover** um registro órfão (violação de FK), a ferramenta tenta **reinserir o registro-pai que
  faltava** a partir do backup FULL agendado mais recente — ou de um **`.bak` escolhido** pelo
  usuário (útil quando o backup está em **outra mídia**) — preservando os registros filhos saudáveis.
  Só cai na **remoção** (destrutiva, com backup de segurança e confirmação) o que não existir em
  backup nenhum; se a recuperação for **parcial/ilegível**, a UI **oferece escolher outro `.bak`**
  antes de qualquer exclusão.
- **Quarentena em vez de exclusão**: os órfãos que sobram não são apagados — saem da tabela e vão,
  na MESMA transação, para o banco `RestoreDB_Quarentena` (com data e constraint de origem), de onde
  podem ser devolvidos. Constraints **desabilitadas** pelo sistema (NOCHECK) são respeitadas: nunca
  geram remoção.
- **Devolução automática das linhas que o `REPAIR_ALLOW_DATA_LOSS` descartar**: depois do reparo, a
  ferramenta mede a perda por tabela (contagem antes × depois + objetos que o reparo diz ter
  desalocado), restaura numa cópia temporária a cadeia de backups **até o instante do reparo**
  (FULL → diferencial → logs → tail-log da rede de segurança) e devolve só as linhas que faltam, por
  chave primária. Só age quando a cópia retrata exatamente o momento anterior ao reparo; senão,
  informa a perda e orienta a revisão manual.
- **Log de transações cheio (9002) resolvido sem perda**: identifica o que segura o log
  (`log_reuse_wait_desc`), faz o backup de log, libera o crescimento, cria um arquivo de log extra
  no disco com mais espaço e religa o banco; transação aberta é **apontada** (sessão, máquina,
  programa), nunca encerrada por conta própria.
- **Arquivos inacessíveis resolvidos sem perda** (causa comum de `RECOVERY_PENDING`): detecta arquivo
  **ausente** (checado no servidor), **sem permissão** (erro 5) ou **preso** (erro 32); aponta o novo
  caminho (`MODIFY FILE`), concede acesso à conta de serviço (como Administrador) e religa.
- **Escalonamento de reparo** do menos ao mais agressivo, **sempre primeiro SEM perda de dados**.
- **Rede de segurança automática antes do reparo destrutivo**: captura o **tail-log WITH
  NO_TRUNCATE** (funciona mesmo com os arquivos de DADOS danificados — junto com a cadeia de
  backups, permite recuperar depois **100%** do que o reparo descartar), backup `COPY_ONLY` e
  cópia física dos arquivos (quando locais).
- **Backup FULL automático pós-reparo destrutivo**: após `REPAIR_ALLOW_DATA_LOSS` ou reconstrução
  de log, a cadeia anterior deixa de ser confiável — a ferramenta cria a **nova base** na hora e
  reajusta as sementes de **IDENTITY** (evita colisões após remoção de linhas).
- **Reparo SEM perda quando a corrupção está só em índices não-clusterizados**
  (reconstrói os índices em vez de descartar dados).
- **Resiliência a erros severos**: um erro **824** (leitura de página corrompida) derruba a conexão
  do SQL Server — a sessão de reparo **reconecta automaticamente** e o fluxo continua; a vaga
  `SINGLE_USER` é **ocupada de fato** pela sessão (`USE`), impedindo que outro processo a roube
  entre os comandos.
- **CHECKDB com fallback `TABLOCK`**: quando o snapshot interno não pode ser criado (disco cheio —
  causa comum das próprias emergências), a verificação repete com `TABLOCK` em vez de abortar; e o
  diagnóstico completo inclui **`DATA_PURITY`** (valores inválidos — relevante em bancos migrados
  do SQL 2000).
- **Restaurar de backup** (full/diferencial/log), incluindo **restauração de página**
  e correção de usuários órfãos — o caminho correto para corrupção de dados.
- **Restauração ponto-no-tempo com cadeia automática**: monta FULL → diferencial → logs a partir
  do histórico (msdb), **valida a continuidade de LSN** e **aborta** se a cadeia estiver quebrada;
  captura o **tail-log** antes de sobrescrever para minimizar a perda.
- **Testar backup (restauração de teste)**: restaura em um banco **descartável** e roda DBCC CHECKDB,
  comprovando que o backup é íntegro e restaurável **sem tocar no banco de produção**.
- **Avaliação da política de backup/recuperação** no diagnóstico: alerta FULL **sem backup de log**,
  última full muito antiga, etc., classificado por **severidade**.
- **Recuperar para cópia (salvamento cirúrgico)**: restaura num banco novo (`..._Recuperado`) para
  **extrair dados** sem sobrescrever o original; assistente que **copia de volta as linhas faltantes**
  (por chave primária, sem duplicar).
- **Realocação automática de arquivos** (`WITH MOVE`): detecta pastas inexistentes no servidor e move
  os arquivos para a pasta padrão da instância.
- **Saúde dos backups**: `RESTORE VERIFYONLY` (com `CHECKSUM` quando disponível) em todo o histórico.
- **Checagens granulares**: `DBCC CHECKTABLE` dirigido, `CHECKIDENT` (reseed de identidade) e
  **verificação rápida `PHYSICAL_ONLY`** para bancos muito grandes.
- **Restauração em STANDBY** (somente leitura entre logs) + **finalizar recuperação**: inspecionar os
  dados antes de aplicar a recuperação definitiva.
- **Mídias avançadas**: backups **divididos em vários arquivos (striped)** restaurados como um só;
  backups **comprimidos** detectados/usados quando a edição permite.
- **Saúde lógica adicional** no diagnóstico: índices **desabilitados** e **dependências quebradas**.
- **Histórico de diagnósticos**: persiste cada verificação e aponta o que é **NOVO** desde a última
  (detecção precoce de corrupção).
- **Reparo por simulação**: pré-visualiza a **perda exata por tabela** rodando o reparo destrutivo
  numa **cópia descartável**, sem tocar na produção — decide-se com conhecimento total.
- **Monitoramento agendado** (via Probe `monitor` no Agendador) com **alerta** quando surge algo novo.
- **Mais situações**: progresso de recuperação em andamento, **anexar `.mdf`** solto, runbook guiado
  para bancos de **sistema**, aviso de **TDE** nos restores.
- **Manutenção preventiva**: desfragmentação de índices + estatísticas; e **causa-raiz** (erros de
  E/S, corrupção recorrente ⇒ suspeita de hardware).
- **Forense de exclusão acidental**: lê o log (`fn_dblog`), identifica **tabela** e linhas do
  DELETE e **devolve só as linhas excluídas sem sobrescrever o banco** (cópia até logo antes + retorno
  por chave primária — o que foi feito depois é preservado); para DROP/TRUNCATE, restaura até logo antes.
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
│   ├─ AvailabilityService.cs           Causas que não são corrupção: log cheio (9002) e arquivos inacessíveis
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
pwsh docs/build-manual-pdf.ps1      # regera docs/MANUAL.pdf a partir de docs/MANUAL.md (Chrome/Edge headless)
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
- **`DBCC CHECKDB ... WITH TABLERESULTS`** (estruturado, com **`DATA_PURITY`** na verificação
  completa) — identifica **objeto e índice** afetados e o **nível mínimo de reparo**; resolve
  nomes de tabela/índice para leitura; se o snapshot interno falhar (disco cheio), repete
  **`WITH TABLOCK`**;
- **`DBCC CHECKCONSTRAINTS`** — integridade **LÓGICA** (chaves estrangeiras e CHECK) que o
  CHECKDB **não** verifica; localiza os **registros órfãos** (FK apontando para PK inexistente).
  **Adiado quando há corrupção física** (ler os dados por índices corrompidos derrubaria a
  conexão — o físico é reparado primeiro);
- **`DBCC CHECKCATALOG`** — consistência dos **metadados** do banco;
- **constraints não confiáveis** (`sys.foreign_keys`/`sys.check_constraints` com `is_not_trusted`)
  — integridade que nunca foi validada (causa comum de FK órfã silenciosa);
- **constraints desabilitadas** (`is_disabled`, `NOCHECK`) — as linhas que as violam são apenas
  **informadas**: o próprio sistema optou por não aplicá-las, então não são corrupção nem são removidas;
- **arquivos do banco** — existência de cada `.mdf`/`.ndf`/`.ldf` checada **no servidor**
  (`xp_fileexist`) e falhas de abertura no log de erros: **ausente** (erro 2/3), **sem permissão**
  (erro 5), **preso por outro processo** (erro 32) — EN e PT;
- **log de transações** — % usado (`sys.dm_db_log_space_usage` no 2012+, `DBCC SQLPERF` no 2008 R2),
  motivo de espera (`log_reuse_wait_desc`) e limites de crescimento de cada arquivo de log;
- **log de erros** (`sp_readerrorlog`, vários arquivos) — discrimina **DADOS** (`.mdf`/`.ndf`)
  × **LOG** (`.ldf`) pelo nome físico do arquivo (independe de idioma) e detecta **9002**
  (log cheio) e **823/824/825** (E/S); a busca inclui os **nomes dos arquivos** (muitas mensagens de E/S citam
  só o caminho, não o nome do banco);
- **topologia** (espelhamento/Always On/log shipping/replicação);
- **espaço em disco** dos volumes do banco (`sys.dm_os_volume_stats`);
- **`PAGE_VERIFY`** — recomenda `CHECKSUM` quando estiver `NONE`/`TORN_PAGE_DETECTION`.

A conclusão define a **ação recomendada** e a UI destaca o caminho certo:

- corrupção **só em índices não-clusterizados** ⇒ **reparo SEM perda** (reconstruir índices);
- corrupção de **dados** ⇒ **restaurar de backup** (ou **restauração de página**, se poucas);
- **integridade lógica** (FK órfã / constraint) com físico íntegro ⇒ **corrigir integridade**
  (remover registros órfãos) ou restaurar de backup para preservar tudo;
- corrupção de **log** ⇒ **reconstruir o log**;
- **arquivo ausente/sem permissão/preso** (banco fora do ar) ⇒ **corrigir acesso aos arquivos e
  religar** — tem prioridade: não é corrupção, e com o arquivo inacessível as demais evidências ficam
  enviesadas;
- **log cheio (9002)** ou prestes a encher ⇒ **resolver log cheio** (backup de log, crescimento,
  arquivo de log extra, religar) — não é corrupção;
- **topologia de alta disponibilidade** ⇒ alerta para tratar antes de reparar.

## Lógica de reparo (escalonamento: do menos ao mais agressivo)

| Estado            | Ação                                                                              | Perda de dados |
|-------------------|-----------------------------------------------------------------------------------|----------------|
| `ONLINE` (verificado) | `DBCC CHECKDB` + `CHECKCONSTRAINTS`; se íntegro, nada a fazer. Se corrompido: `SINGLE_USER` → `REPAIR_REBUILD` (sem perda) → **(com confirmação)** `REPAIR_ALLOW_DATA_LOSS` → reconciliar FKs | Não → (talvez) |
| `OFFLINE`         | `SET ONLINE` → `SET MULTI_USER`                                                    | Não            |
| `RESTORING`/`RECOVERING` | `RESTORE DATABASE ... WITH RECOVERY`                                        | Não            |
| `RECOVERY_PENDING`| Tenta `SET ONLINE`; se falhar, cai no fluxo de emergência abaixo                   | Não → (talvez) |
| Arquivo **inacessível** (diagnóstico) | *(se movido)* `SET OFFLINE` → `MODIFY FILE (FILENAME = novo caminho)`; *(sem permissão, local + Administrador)* `icacls /grant <conta do SQL>` → `SET ONLINE` | **Não** |
| **Log cheio** (9002) | `BACKUP LOG` (se `LOG_BACKUP`) / `CHECKPOINT` → `MODIFY FILE (MAXSIZE = UNLIMITED, FILEGROWTH)` → `ADD LOG FILE` no disco com mais espaço → `SET ONLINE` (se fora do ar) | **Não** |
| `SUSPECT`/`EMERGENCY` | `SET EMERGENCY` → `SET SINGLE_USER WITH ROLLBACK IMMEDIATE` → `DBCC CHECKDB` (diagnóstico) → `SET ONLINE` (recuperação) → `REPAIR_REBUILD` → verificação | Não |
| ↳ se restar só corrupção de **índice não-clusterizado** | `ALTER INDEX ... REBUILD` (reconstrói os índices afetados) → verificação | **Não** |
| ↳ se o reparo seguro não bastar | **(com confirmação)** `EMERGENCY` + `SINGLE_USER` → **tail-log `NO_TRUNCATE`** + backup `COPY_ONLY` + **cópia física** dos arquivos → `REPAIR_ALLOW_DATA_LOSS` → `SET ONLINE` (se ficou em `EMERGENCY`) → verificação → **devolver do backup as linhas descartadas** (cópia até o instante do reparo) → reconciliar FKs (pai do backup; órfão restante → **quarentena**) + reseed de IDENTITY → `MULTI_USER` → **backup FULL pós-reparo** | **Sim*** |
| **Corrupção de dados** (recomendado) | **Restaurar de backup** (`RESTORE`/`RESTORE ... PAGE`) → corrigir usuários órfãos | Não* |

> \* O `REPAIR_ALLOW_DATA_LOSS` descarta o que está corrompido, **mas** o tail-log capturado
> antes (`WITH NO_TRUNCATE` — funciona mesmo com o `.mdf` danificado, basta o `.ldf` legível)
> permite, havendo cadeia de backups, **restaurar ponto-no-tempo até logo antes do reparo e
> recuperar 100% dos dados** (validado em engine real — ver a seção de validação).
>
> **Detalhe validado em engine real:** o SQL Server rejeita `REPAIR_REBUILD` com o
> banco em modo `EMERGENCY` (Msg 7901). Por isso o fluxo tenta a **recuperação
> normal (`SET ONLINE`) primeiro**; com o banco ONLINE + `SINGLE_USER`, o
> `REPAIR_REBUILD` (sem perda de dados) passa a ser permitido. O
> `REPAIR_ALLOW_DATA_LOSS`, ao contrário, **é** permitido em `EMERGENCY`.
>
> **Outro detalhe validado:** um erro **824** (severidade 24) ao ler uma página corrompida
> **derruba a conexão** — a `SqlSession` reconecta automaticamente antes de cada comando, e as
> checagens lógicas (`CHECKCONSTRAINTS`, que lê os dados pelos índices) são **adiadas** quando o
> CHECKDB acusa corrupção física, para não matar a sessão no meio do reparo.

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

- Toda a sequência roda na **mesma conexão** (`SqlSession`); nos fluxos em que o banco
  permanece ONLINE, a sessão **ocupa a vaga `SINGLE_USER`** com um `USE` durável — sem isso,
  qualquer aplicação poderia roubar a única vaga entre um comando e outro. Se um erro severo
  (824) derrubar a conexão, a sessão **reconecta automaticamente**.
- A aplicação conecta-se sempre em **`master`** — nunca diretamente no banco
  quebrado.
- Antes do reparo destrutivo, a **rede de segurança**: `BACKUP LOG ... WITH NO_TRUNCATE,
  CONTINUE_AFTER_ERROR` (tail-log — funciona mesmo com os dados danificados),
  `BACKUP DATABASE ... WITH COPY_ONLY` e cópia física dos arquivos (quando locais). Tudo
  best-effort: o que não for possível é avisado, sem impedir o reparo autorizado.
- Após um reparo destrutivo **bem-sucedido** (ou reconstrução de log), um **backup FULL novo**
  é criado automaticamente — a cadeia de backups anterior deixa de ser confiável — e as
  sementes de **IDENTITY** são reajustadas.
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

### Restauração ponto-no-tempo, tail-log e teste de backup

Para corrupção de DADOS, **restaurar de backup** é o caminho correto — e a ferramenta vai além do
restore de peça única:

- **Cadeia automática (`RestoreToPointInTimeAsync`)** — a partir do histórico em `msdb`, monta a
  sequência **FULL → diferencial → logs** até um instante alvo (ou o ponto mais recente). A montagem
  (`BackupChainPlanner`) **valida a continuidade de LSN**: o diferencial precisa casar com o
  `checkpoint_lsn` do full e cada log precisa encadear (`first_lsn ≤ âncora < last_lsn`). Se houver
  **quebra na cadeia** (log faltando, diferencial órfão), a operação é **abortada antes de executar**,
  explicando o que falta — nunca deixa o banco num estado pior.
- **Tail-log** — antes de sobrescrever, faz `BACKUP LOG ... WITH NORECOVERY, NO_TRUNCATE,
  CONTINUE_AFTER_ERROR` para capturar as transações mais recentes e anexá-las como último elo da
  cadeia (minimiza o RPO). O `NO_TRUNCATE` permite a captura **mesmo com os arquivos de dados
  danificados/offline** — basta o `.ldf` legível.
- **Testar backup (`TestRestoreAsync`)** — restaura a cadeia para um banco **descartável** (com
  `MOVE` para a pasta do `tempdb`), roda `DBCC CHECKDB` e **descarta** o banco de teste ao final
  (mesmo em falha). Prova a restaurabilidade **sem tocar na produção**.

### Recuperar para cópia e salvamento cirúrgico

Quando a maior parte do banco está sã e só alguns registros se perderam, sobrescrever tudo é
desnecessário. **`RestoreAsCopyAsync`** restaura a cadeia mais recente para um banco de CÓPIA
(`{original}_Recuperado`, com arquivos realocados e nomes novos para nunca colidir) **sem tocar no
original**. Em seguida, **`SalvageTableAsync`** copia da cópia para o banco vivo apenas as linhas
**que faltam** (por chave primária, preservando `IDENTITY`, sem duplicar). Assim dá para recuperar
tabelas/linhas pontuais a partir de um backup íntegro.

### Realocação automática de arquivos (WITH MOVE)

Em `RestoreRequest.AutoRelocate`, a restauração lê os arquivos do backup (`FILELISTONLY`), checa no
**servidor** (`xp_fileexist`) se a pasta original existe e, quando não, gera os `MOVE`
automaticamente para a pasta padrão da instância (derivada do `master`). O `RestorePlanner` que
decide isso é lógica pura e coberta por testes.

### Checagens granulares (B1)

Além do CHECKDB completo: **`DBCC CHECKTABLE`** dirigido a um objeto (mais rápido), **`DBCC
CHECKIDENT ... RESEED`** para reajustar sementes de identidade após remoção de linhas (também
executado **automaticamente** após um reparo com perda), e a opção **`PHYSICAL_ONLY`** no
diagnóstico (varredura rápida para bancos muito grandes). O diagnóstico completo roda com
**`DATA_PURITY`** (valida também os valores das colunas — em bancos migrados do SQL 2000 essa
checagem não é automática).

### Restauração em STANDBY e mídias avançadas

- **STANDBY** (`RestoreToStandbyAsync`): restaura a cadeia deixando o banco em **somente leitura**,
  consultável entre os logs, para achar o ponto certo; depois **`FinalizeRecoveryAsync`** aplica o
  `RESTORE … WITH RECOVERY` definitivo. O arquivo de undo é criado na pasta padrão da instância.
- **Striped** (vários arquivos por backup): o histórico agrupa os arquivos do mesmo conjunto e a
  restauração monta `FROM DISK = …, DISK = …` automaticamente.
- **Compressão**: backups comprimidos são detectados (histórico/`HEADERONLY`) e os backups que a
  própria ferramenta gera (tail-log, COPY_ONLY de segurança) usam `COMPRESSION` quando a edição
  suporta (Standard/Enterprise; Express não).

### Saúde lógica adicional e histórico (detecção precoce)

O diagnóstico agrega achados de **saúde lógica** (índices `is_disabled`, dependências quebradas em
`sys.sql_expression_dependencies`, constraints não confiáveis), classificados por severidade. Cada
diagnóstico é **persistido** (`%LOCALAPPDATA%\RestoreDB\History`) e comparado com o anterior: o que
**piorou** (corrupção nova, mais violações de FK, mais páginas suspeitas) é destacado como **NOVO
desde a última verificação**.

### Avaliação da política de backup (RPO)

O diagnóstico cruza o **modelo de recuperação** com o **histórico** (`BackupPolicyEvaluator`) e
classifica os achados por severidade: **Crítico** (sem nenhum FULL — irrestaurável), **Alto**
(modelo FULL/BULK_LOGGED **sem backup de log** → log cresce e perda potencial grande; ou última FULL
> 30 dias), **Médio** (última FULL > 7 dias), **Informativo** (modelo SIMPLE → sem ponto-no-tempo).

### Banco ONLINE porém corrompido

Nem toda corrupção muda o `state_desc`: um banco pode permanecer **`ONLINE`** com páginas
corrompidas (reveladas só pelo `DBCC CHECKDB`) ou com **integridade lógica** quebrada. Por isso o
**Diagnóstico** roda em qualquer banco e o reparo passa a depender do que o diagnóstico encontra.
Quando há corrupção física num banco já `ONLINE`, o reparo é feito em **`SINGLE_USER`** (sem
precisar de `EMERGENCY`): tenta `REPAIR_REBUILD` (sem perda) e, só com confirmação explícita,
`REPAIR_ALLOW_DATA_LOSS` — preferindo sempre **restaurar de backup** quando os dados estão
corrompidos.

### Corrigir integridade lógica (registros órfãos de chave estrangeira)

O `DBCC CHECKDB` valida a estrutura **física**, mas **não** verifica chaves estrangeiras. Uma
corrupção (ou um `REPAIR_ALLOW_DATA_LOSS` anterior, que desaloca páginas **sem** respeitar
constraints) pode deixar uma linha cuja FK aponta para uma PK **inexistente** (registro órfão). O
**`DBCC CHECKCONSTRAINTS`** detecta essas violações e devolve o predicado exato das linhas
violadoras — que carrega o **valor da chave** que falta no pai. A opção **"Corrigir integridade
(remover registros órfãos)"**:

- obtém acesso exclusivo (`SINGLE_USER`) e faz **backup/cópia de segurança** antes;
- **tenta primeiro recuperar o registro-pai** que faltava, a partir de um backup (ver abaixo),
  preservando os registros filhos saudáveis;
- só então **remove** as linhas órfãs remanescentes (apenas violações de **FK**; violações de
  **CHECK** são reportadas para revisão manual), usando o predicado gerado pelo próprio SQL Server;
- **reverifica** com `CHECKCONSTRAINTS` ao final.

Além disso, após qualquer `REPAIR_ALLOW_DATA_LOSS`, a ferramenta **reconcilia automaticamente** as
chaves estrangeiras órfãs que a desalocação possa ter criado (mesma recuperação de pai + remoção do
que sobrar) — deixando o banco **logicamente** consistente, não só fisicamente.

#### Recuperação automática do registro-pai a partir de backup

Quando uma linha filha ficou órfã porque o **pai** foi descartado (por corrupção ou por um
`REPAIR_ALLOW_DATA_LOSS`), o dado filho geralmente está **saudável** — só perdeu a referência.
Como o `CHECKCONSTRAINTS` entrega o valor **exato** da chave ausente, a ferramenta consegue trazer
o pai de volta em vez de descartar o filho:

1. restaura o **último backup FULL agendado** (não-`COPY_ONLY`, **somente o FULL, sem logs** — para
   não reproduzir a própria remoção do pai) em um **banco paralelo descartável**
   (`..._OrphanRecovery_*`), via `RestoreService.RestoreAsCopyAsync`;
2. resolve, pelo catálogo (`sys.foreign_keys`/`sys.foreign_key_columns`), a **tabela-pai** e o
   mapeamento coluna-filha → coluna-pai (cobre **chaves compostas**), reescreve o predicado para o
   pai e **reinsere** a linha-pai no banco vivo (`INSERT … SELECT` cross-database, com
   `IDENTITY_INSERT` quando há identidade) — só o que faltava, sem duplicar (violação de PK é tratada
   como sucesso);
3. reverifica e **só remove** os órfãos cujo pai não existir em backup nenhum; o banco paralelo é
   sempre descartado ao final.

**Escolha do backup e outra mídia.** Por padrão a recuperação usa o histórico do `msdb`. Nos painéis
de correção e de reparo com perda há a opção **"Recuperação de registros-pai" → "📁 Escolher .bak…"**,
para apontar um arquivo específico — indispensável quando o backup está em **outra mídia** que o
`msdb` nem conhece. Se a recuperação **falhar ou for parcial** (arquivo ilegível, sem FULL, ou sem o
pai procurado), a ferramenta **pergunta antes de remover** se você quer **escolher outro `.bak`** e
tentar de novo, mirando só os órfãos restantes — e só remove quando você optar por prosseguir.

> ⚠️ O arquivo escolhido precisa ser **legível pela conta de serviço do SQL Server** (o `RESTORE`
> roda no servidor), não pelo aplicativo. Para servidor remoto, use um caminho de rede (UNC)
> acessível à conta de serviço.

Para preservar 100% dos registros, **restaurar de backup** continua sendo o caminho preferido.

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
A cadeia é montada pelo **`BackupChainPlanner`** (validação de LSN): usa o último FULL, o
**diferencial** aplicável (encurta drasticamente a fila de logs em cadeias longas), os logs e um
`BACKUP LOG` novo como elo final — e **aborta com explicação** se a cadeia estiver quebrada.
Na UI, quando o diagnóstico recomenda a restauração de página, o campo `fileId:pageId` já vem
**pré-preenchido** a partir de `msdb.suspect_pages`.

### Proteções adicionais de integridade

- **Gatilhos não disparam** ao devolver linhas (salvamento, registro-pai, pós-reparo) nem ao mover
  órfãos para a quarentena: a escrita roda numa transação com `DISABLE TRIGGER` (o bloqueio de
  esquema impede outras sessões de gravar sem gatilho nesse intervalo) e o `ROLLBACK` desfaz tudo em
  caso de erro. Essas linhas já existiram — seus efeitos de negócio (estoque, auditoria...) já ocorreram.
- **Reconstrução de índice não deixa FK desligada**: o `ALTER INDEX … DISABLE` de um índice único/PK
  referenciado desliga as FKs dependentes; elas são reativadas `WITH CHECK` ao final.
- **Restauração com a aplicação conectada**: as conexões são encerradas (`SINGLE_USER WITH ROLLBACK
  IMMEDIATE`) no mesmo lote do `RESTORE`/tail-log, na mesma sessão (de outra, o SQL Server recusa com
  a Msg 3101), e o banco volta em `MULTI_USER`.
- **Plano de restauração confiável**: ignora backups cujo arquivo não existe mais no servidor e as
  cópias `COPY_ONLY` da própria ferramenta (estado já danificado / simulação apagada); o histórico não
  é mais cortado em 300 conjuntos (cadeias com log a cada 15 min eram vistas como "quebradas"); o
  tail-log só é aplicado quando o alvo do ponto-no-tempo está além dos backups existentes.

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
de corrupção só-índice (`CheckDbReport`), topologia/disco, montagem da cadeia de backups (inclusive
descarte das cópias de segurança da própria ferramenta), conclusões do diagnóstico (arquivo
inacessível, log cheio), hora do `fn_dblog`/tabela afetada, classificação das falhas de abertura de
arquivo (EN/PT), `object ID` citados pelo reparo e utilitários de domínio.

```powershell
dotnet test RestoreDB.slnx
```

**Testes de integração** ([`tests/RestoreDB.IntegrationTests`](tests/RestoreDB.IntegrationTests)) —
exercitam os serviços reais contra uma instância SQL: conexão/`ServerInfo`, reparo `OFFLINE`,
`EMERGENCY` e `RESTORING`, diagnóstico estruturado (CHECKDB/`suspect_pages`/topologia/disco),
restauração de backup (revertendo perda de dados), **recuperação de registros-pai órfãos a partir
de backup** (automática, com `.bak` escolhido e com o novo backup interativo via callback),
`HEADERONLY`/`FILELISTONLY` e os caminhos novos: **quarentena** de órfãos, constraints
**desabilitadas** respeitadas, salvamento **sem disparar gatilhos**, **FKs reativadas** após
reconstruir índice, **exclusão acidental desfeita sem sobrescrever**, restauração **com a aplicação
conectada**, plano que **ignora backup apagado**, ponto-no-tempo **sem aplicar o tail-log** quando
desnecessário, **log cheio (9002)** resolvido e **arquivo ausente** realocado. Cada teste cria
e **derruba** seu próprio banco `RestoreDB_IT_*` (isolado) e limpa os backups gerados — inclusive os
que a própria ferramenta criou (tail-log, cópias de segurança) e as tabelas de quarentena. Sem as
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

> **Situação atual: 121 testes de lógica + 44 de integração aprovados nas duas versões**:
>
> | Instância | Versão | Autenticação | Resultado |
> |---|---|---|---|
> | `SQL2008-TESTE` | **SQL Server 2008 R2 SP3 Express** (10.50.6220) | SQL (`sa`) | 44/44 |
> | `SQL2025-TESTE` | **SQL Server 2025 Developer** (17.0) | Windows | 44/44 |
>
> Também em produção, contra **SQL Server 2008 R2 SP3 (Express Edition, 10.50.6000.34)**. O
> diagnóstico (somente leitura) foi conferido nas duas versões sobre um banco real de teste (≈900 MB,
> sistema de varejo): sem falso positivo nas checagens de arquivos, log e constraints — no 2008 o uso
> do log é lido pelo `DBCC SQLPERF` (a DMV `sys.dm_db_log_space_usage` só existe do 2012 em diante).
>
> Os fluxos de reparo foram exercitados com **corrupção física real** (`DBCC WRITEPAGE`) sobre cópias de
> bancos de produção de teste (≈900 MB). A **recuperação de registro-pai a partir de backup** foi
> atestada **ponta a ponta nas duas versões**: remover um registro-pai real, recuperá-lo do backup
> (histórico e `.bak` escolhido) e **preservar o registro filho** — com efeito líquido zero. O
> `Microsoft.Data.SqlClient` conecta-se ao 2008 negociando TLS 1.0 (emite apenas um **aviso**
> benigno; `Encrypt=Optional` torna a conexão possível).

### Validação com corrupção real

Os caminhos destrutivos foram exercitados contra **corrupção física real** (via `DBCC WRITEPAGE`)
num banco de produção de teste, com backup prévio e restauração ao final:

- **Índice não-clusterizado corrompido** ⇒ a ferramenta detecta "só índice", reconstrói
  (`DISABLE` + `REBUILD`) e verifica — **zero perda de dados** (checksum idêntico ao original).
  O erro 824 derrubou a conexão da sessão durante a checagem e a **reconexão automática**
  manteve o fluxo até o fim.
- **Página de dados corrompida** ⇒ **restauração de página** com cadeia automática
  (FULL + **diferencial** + log novo) — **zero perda** (contagem e checksum idênticos).
- **Página de dados corrompida** + reparo COM perda autorizado ⇒ a rede de segurança capturou o
  **tail-log `NO_TRUNCATE`** e o backup `COPY_ONLY`; o `REPAIR_ALLOW_DATA_LOSS` descartou 106
  linhas e o **backup FULL pós-reparo** foi criado. Em seguida, a **restauração ponto-no-tempo
  usando o tail-log preservado** (STOPAT logo antes do reparo) **recuperou 100% dos dados**
  (contagem e checksum idênticos ao original) — a perda do reparo é reversível quando há cadeia.
- **Restaurar de backup** ⇒ recupera 100% dos dados; corrige usuários órfãos.
- O diagnóstico **reconcilia histórico × estado atual**: entradas antigas de `msdb.suspect_pages`
  e do log de erros não geram falso positivo quando o `DBCC CHECKDB` ao vivo está limpo.
- **Reconstrução de log (último recurso)** sem privilégio de Administrador ⇒ a ferramenta **aborta
  ANTES de desanexar** (pré-checagem de elevação), deixa o banco **intacto** e imprime as instruções
  de recuperação manual — confirmando a garantia de "não piorar o estado".
- Estados `OFFLINE` e `RESTORING` ⇒ religados sem perda (`SET ONLINE` / `RESTORE WITH RECOVERY`).

> ⚠️ **Pendente:** a **devolução automática das linhas descartadas pelo `REPAIR_ALLOW_DATA_LOSS`**
> ainda não foi exercitada ponta a ponta com corrupção física real. Suas peças estão cobertas por
> testes — cópia restaurada até um instante (cadeia + tail-log), reinserção por chave primária com
> gatilhos desligados, leitura dos `object ID` nas mensagens do reparo —, mas convém repetir o cenário
> "página de dados corrompida + reparo COM perda" acima e conferir a contagem/checksum final.

O utilitário [`tools/RestoreDB.Probe`](tools/RestoreDB.Probe) é um runner de linha de comando que
invoca os serviços reais (`diagnose`/`repair`/`repair-indexes`/`fix-constraints`/`simulate-repair`/`monitor`/`maintain`/`fragmentation`/`forensic`/`attach`/`restore`/`restore-pit`/`restore-pages`/`restore-standby`/`finalize-recovery`/`test-restore`/`restore-copy`/`salvage-table`/`verify-backups`/`check-table`/`reseed`/`history`/`rebuild-log`/`resolve-log`/`fix-files`/`recover-deleted`) — usado nessa
validação e útil para operar a ferramenta sem a interface gráfica.

```powershell
# Autenticação do Windows:
$env:PROBE_SERVER = "localhost"
# ...ou autenticação SQL:
$env:PROBE_USER = "sa"
$env:PROBE_PASS = "********"

dotnet run --project tools/RestoreDB.Probe -- diagnose MeuBanco
dotnet run --project tools/RestoreDB.Probe -- resolve-log MeuBanco
dotnet run --project tools/RestoreDB.Probe -- fix-files MeuBanco MeuBanco_log=E:\Dados\MeuBanco_log.ldf
dotnet run --project tools/RestoreDB.Probe -- recover-deleted MeuBanco 2026-09-23T14:05:00 dbo.NotaFiscal
```
