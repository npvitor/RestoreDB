# Manual de Utilização — RestoreDB

**RestoreDB — Diagnóstico e Reparo de Bancos SQL Server (Módulo de Faturamento)**

Ferramenta para desktop (Windows) que ajuda a **diagnosticar e reparar** bancos de dados
SQL Server que ficaram em estados problemáticos — `SUSPECT`, `RECOVERY_PENDING`,
`EMERGENCY`, `RECOVERING`, `RESTORING` e `OFFLINE` — com linguagem acessível a usuários
não técnicos. Compatível com **SQL Server 2008 R2** e versões superiores.

> **Princípio central:** a ferramenta sempre tenta **primeiro o caminho SEM perda de dados**.
> Qualquer operação que possa descartar dados exige **confirmação explícita** do usuário.

---

## Sumário

1. [Antes de começar (requisitos)](#1-antes-de-começar-requisitos)
2. [Conceitos importantes](#2-conceitos-importantes)
3. [Tela de conexão](#3-tela-de-conexão)
4. [Tela principal — visão geral](#4-tela-principal--visão-geral)
5. [Entendendo os estados dos bancos](#5-entendendo-os-estados-dos-bancos)
6. [Passo a passo do reparo](#6-passo-a-passo-do-reparo)
7. [As opções de reparo em detalhe](#7-as-opções-de-reparo-em-detalhe)
8. [Recursos auxiliares](#8-recursos-auxiliares)
9. [Modo escuro](#9-modo-escuro)
10. [Onde ficam os registros (logs)](#10-onde-ficam-os-registros-logs)
11. [Segurança e proteções automáticas](#11-segurança-e-proteções-automáticas)
12. [Boas práticas](#12-boas-práticas)
13. [Solução de problemas (FAQ)](#13-solução-de-problemas-faq)
14. [Glossário rápido](#14-glossário-rápido)

---

## 1. Antes de começar (requisitos)

- **Windows** com o aplicativo RestoreDB instalado.
- **Acesso a uma instância do SQL Server** (2008 R2 ou superior).
- Um **login com permissão de `sysadmin`** na instância. As operações de reparo
  (`DBCC CHECKDB`, `ALTER DATABASE`, `RESTORE`) exigem esse nível de acesso.
- Para a operação de **reconstrução de log** (último recurso), execute a ferramenta
  **como Administrador** do Windows (veja a [seção 7.5](#75-reconstruir-o-log-último-recurso)).

> A ferramenta conecta-se sempre ao catálogo **`master`** — nunca diretamente ao banco
> com problema. Isso é o procedimento correto e seguro.

---

## 2. Conceitos importantes

- **Diagnóstico primeiro, reparo depois.** Para qualquer banco com problema, a única
  ação inicial é **diagnosticar** (somente leitura, não altera nada). Só depois do
  diagnóstico a ferramenta libera as opções de reparo — e mostra **apenas as que fazem
  sentido para aquela situação**.
- **Sem perda de dados sempre que possível.** A ferramenta tenta religar/recuperar e, se
  preciso, reconstruir índices, antes de qualquer caminho destrutivo.
- **Confirmação explícita** para operações que podem perder dados (caixa de seleção +
  diálogo de confirmação).
- **Tudo é registrado.** Cada comando aparece no painel **Registro de operações** e é
  gravado em arquivo (veja a [seção 10](#10-onde-ficam-os-registros-logs)).

---

## 3. Tela de conexão

Ao abrir a ferramenta, aparece a tela **RestoreDB — Conexão**.

| Campo | O que informar |
|---|---|
| **SERVIDOR** | Nome ou endereço da instância. Ex.: `localhost`, `.\SQLEXPRESS`, `192.168.0.10,1433` |
| **Usar autenticação do Windows (Integrated Security)** | Marque para entrar com o usuário do Windows. Ao marcar, os campos Usuário/Senha são desativados. |
| **USUÁRIO** / **SENHA** | Usados apenas na autenticação do SQL Server (quando a caixa acima está **desmarcada**). Ex.: usuário `sa`. |

Clique em **Conectar**.

**Pré-checagem automática:** logo após conectar, a ferramenta verifica as condições e
**avisa** caso:

- o login **não seja `sysadmin`** — os reparos provavelmente falharão; conecte-se com um
  login adequado; ou
- a instância seja **Azure SQL Database** — onde os comandos de reparo locais não se aplicam.

Você pode optar por continuar mesmo assim, mas o ideal é conectar com um login `sysadmin`.

> Os demais parâmetros da conexão (tempo limite, criptografia, *application name*, etc.)
> são pré-definidos e compatíveis com o TLS antigo do **SQL Server 2008 R2**.

---

## 4. Tela principal — visão geral

Após conectar, abre a tela principal, dividida em quatro áreas:

```
┌──────────────────────────────────────────────────────────────────────────┐
│ Barra superior:  info do servidor   [Tema] [Cancelar] [Atualizar] [Trocar] │
├───────────────────────────────────────┬────────────────────────────────────┤
│  LISTA DE BANCOS                       │  DETALHES E REPARO                  │
│  Banco | Estado | Situação             │  (estado, ação recomendada,         │
│  (bancos com problema em vermelho)     │   diagnóstico e opções de reparo)   │
├───────────────────────────────────────┴────────────────────────────────────┤
│  REGISTRO DE OPERAÇÕES (log ao vivo)            [Exportar relatório] [Limpar]│
├──────────────────────────────────────────────────────────────────────────┤
│  Barra de status                                  caminho do arquivo de log │
└──────────────────────────────────────────────────────────────────────────┘
```

- **Barra superior:** mostra `Servidor`, `Catálogo: master`, edição/versão do SQL Server e
  se o login é `sysadmin`. À direita: alternância de **Tema**, **Cancelar** (interrompe a
  operação em andamento), **Atualizar lista** e **Trocar conexão**.
- **Lista de bancos:** todas as bases e seu **Estado** e **Situação** (descrição amigável).
  Bancos em estado problemático ficam **destacados em vermelho**.
- **Detalhes e reparo:** painel da direita; reflete o banco selecionado.
- **Registro de operações:** cada passo executado, colorido por tipo (informação, comando,
  sucesso, aviso, erro).
- **Barra de status:** mensagem atual e o caminho do arquivo de log do dia.

---

## 5. Entendendo os estados dos bancos

A coluna **Situação** já traz a explicação em linguagem simples. Resumo:

| Estado | Situação | É problema? | Caminho típico |
|---|---|---|---|
| **ONLINE** | Disponível e saudável. | Não | Nenhuma ação |
| **OFFLINE** | Desligado manualmente. Pode ser religado sem perda de dados. | Sim | Religar (modo seguro) |
| **RESTORING** | Em restauração / aguardando finalização da recuperação. | Sim | Finalizar recuperação (sem perda) |
| **RECOVERING** | Em recuperação automática. | Sim | Finalizar recuperação (sem perda) |
| **RECOVERY_PENDING** | Recuperação pendente — geralmente falta de arquivo/recurso. | Sim | Religar; se não bastar, diagnóstico + reparo |
| **SUSPECT** | Suspeito — possível corrupção. | Sim | Diagnóstico + reparo (1º sem perda) |
| **EMERGENCY** | Modo de emergência (somente leitura, usado durante o reparo). | Sim | Diagnóstico + reparo (1º sem perda) |

---

## 6. Passo a passo do reparo

### Passo 1 — Selecione o banco
Clique no banco desejado na lista. O painel **Detalhes e reparo** mostra o **nome**, o
**ESTADO ATUAL** (em destaque colorido) e a **AÇÃO RECOMENDADA**.

- Se o banco estiver **saudável (ONLINE)**, aparece a mensagem de que nenhum reparo é
  necessário.
- Se for um **banco de sistema** (`master`, `model`, `msdb`, `tempdb`) com problema, a
  ferramenta avisa que o reparo destrutivo automático **não é permitido** — acione o DBA.

### Passo 2 — Diagnostique (não altera o banco)
Para bancos com problema, clique em **“Diagnosticar (sem alterar o banco)”**.
O diagnóstico é **somente leitura**: ele cruza fontes oficiais (páginas suspeitas,
`DBCC CHECKDB`, log de erros, topologia, espaço em disco) e **não modifica nada**.

### Passo 3 — Leia o resultado
Aparece o painel **RESULTADO DO DIAGNÓSTICO**, com:

- um resumo da situação;
- a linha **“→ Ação recomendada: …”** (o caminho correto para o caso);
- a recomendação detalhada.

A cor indica a gravidade (verde = seguro/sem perda, amarelo = atenção, vermelho =
corrupção de dados).

### Passo 4 — Execute a opção recomendada
Abaixo de **“COMO RESOLVER”**, a ferramenta exibe **apenas a(s) opção(ões) aplicável(is)**
ao diagnóstico — guiando você ao caminho certo. Veja cada uma na próxima seção.

> A qualquer momento você pode usar **“Ver script planejado (não executa nada)”** para
> revisar o T-SQL que *seria* executado (útil para um DBA conferir ou rodar manualmente).

---

## 7. As opções de reparo em detalhe

A opção que aparece depende do que o diagnóstico encontrou:

### 7.1. Reparar índices (sem perda de dados)
**Quando aparece:** a corrupção está **restrita a índices não-clusterizados** (que são
redundantes e podem ser reconstruídos sem perder nenhum dado).
**O que faz:** desabilita e reconstrói (`ALTER INDEX … REBUILD`) os índices afetados e
verifica o resultado com `DBCC CHECKDB`.
**Resultado:** banco consistente, **zero perda de dados**.
Botão: **“Reparar índices (sem perda de dados)”**.

### 7.2. Reparar (modo seguro, sem perda de dados)
**Quando aparece:** estados `OFFLINE`, `RESTORING`, `RECOVERING`, `RECOVERY_PENDING` e
casos gerais em que o caminho seguro é suficiente.
**O que faz (escalonamento automático, do mais leve ao necessário):**
- `OFFLINE` → religa (`SET ONLINE`);
- `RESTORING`/`RECOVERING` → finaliza a recuperação (`RESTORE … WITH RECOVERY`);
- `SUSPECT`/`EMERGENCY` → `EMERGENCY` → acesso exclusivo → `DBCC CHECKDB` →
  recuperação normal → `REPAIR_REBUILD` → verificação.
**Resultado:** sem perda de dados. Se o caminho seguro **não** for suficiente, a
ferramenta **não** força nada: ela passa a oferecer o reparo com perda de dados, que
**exige sua confirmação** (veja 7.4).
Botão: **“Reparar (modo seguro, sem perda de dados)”**.

### 7.3. Restaurar de backup
**Quando aparece:** o diagnóstico identificou **corrupção nos dados** (`.mdf`/`.ndf`) —
nesse caso, restaurar é o caminho correto (o reparo destrutivo apenas *descartaria* o que
está corrompido).
**Como usar:**
1. Selecione um backup do **histórico** (lido de `msdb`) **ou** clique em
   **“Escolher arquivo .bak…”** para apontar um arquivo manualmente.
2. Deixe **“Substituir o banco existente (REPLACE)”** marcado para sobrescrever o banco
   atual pelos dados íntegros do backup.
3. Clique em **“Restaurar o backup selecionado”** e confirme.
A ferramenta verifica a mídia antes de aplicar e, ao final, **corrige usuários órfãos**.
**Resultado:** recuperação dos dados a partir do backup escolhido.

### 7.4. Reparo COM possível perda de dados (exige confirmação)
**Quando aparece:** o reparo seguro não resolveu e não há (ou não se quer) restaurar de
backup.
**O que faz:** tenta um **backup de segurança** e então executa
`DBCC CHECKDB … REPAIR_ALLOW_DATA_LOSS` em modo de emergência, que **descarta** as páginas
corrompidas para tornar o banco consistente.
**Como usar:** marque **“Estou ciente e autorizo o reparo com possível perda de dados.”**,
clique em **“Executar reparo COM perda de dados”** e confirme o diálogo.
> ⚠️ **Pode haver perda de dados irreversível.** Prefira sempre **restaurar de backup**
> quando houver um disponível.

### 7.5. Reconstruir o log (último recurso)
**Quando aparece:** o problema está no **log de transações** (`.ldf` corrompido/ausente,
ex.: `RECOVERY_PENDING`/`SUSPECT` por causa do log) e o reparo não resolveu.
**O que faz:** coloca o banco em emergência, **desanexa**, **remove** o `.ldf` antigo
(preservando-o com a extensão `.corrupt_AAAAMMDDHHMMSS`) e o **recria**
(`FOR ATTACH_REBUILD_LOG`), forçando o retorno a `ONLINE`.
**Requisitos:**
- os arquivos do banco precisam estar **acessíveis nesta máquina** (servidor local);
- execute a ferramenta **como Administrador** do Windows.

> 🔒 **Proteção:** sem privilégio de Administrador, a operação é **abortada ANTES de
> desanexar** — o banco **não é alterado** — e a ferramenta exibe as instruções de
> recuperação manual. Marque **“Entendo os riscos e autorizo reconstruir o log.”** para
> habilitar o botão **“Reconstruir log (recriar .ldf)”**.
>
> **Alternativa sem Administrador:** quando o problema é o log, o **reparo com perda de
> dados** (7.4) também reconstrói o log — inteiramente no servidor, sem mexer em arquivos.

---

## 8. Recursos auxiliares

- **Atualizar lista:** relê os bancos e seus estados.
- **Trocar conexão:** volta à tela de conexão (para outro servidor/login).
- **Cancelar:** interrompe a operação em andamento com segurança.
- **Ver script planejado (não executa nada):** mostra o T-SQL que seria executado para o
  estado atual — ótimo para revisão por um DBA.
- **Exportar relatório:** salva o **Registro de operações** em um arquivo **HTML** (anexe
  a um chamado/atendimento).
- **Limpar:** limpa o registro exibido na tela (não apaga o arquivo de log).

---

## 9. Modo escuro

Ao **iniciar**, a ferramenta adota automaticamente o **tema do Windows** (claro ou escuro),
conforme a opção *Configurações → Cores → “Escolha o modo do aplicativo”* do sistema.

Você pode alterná-lo a qualquer momento pelo botão **“🌙 Tema escuro”** (na barra superior),
que passa a exibir **“☀ Tema claro”** quando o modo escuro está ativo. A escolha manual vale
para toda a aplicação (inclusive ao trocar de conexão) durante o uso.

---

## 10. Onde ficam os registros (logs)

Todas as ações são gravadas, com data e hora, em:

```
%LOCALAPPDATA%\RestoreDB\Logs\repair-AAAAMMDD.log
```

(um arquivo por dia). O caminho do arquivo do dia também aparece na **barra de status**.
Para entregar o histórico de uma sessão a outra pessoa, use **Exportar relatório** (HTML).

---

## 11. Segurança e proteções automáticas

- **Guarda de topologia:** se o banco participa de **espelhamento**, **Always On (AG)**,
  **log shipping** ou **replicação**, a ferramenta **bloqueia** o reparo destrutivo e
  orienta a tratar a topologia primeiro (failover, remover do AG, etc.). Um aviso amarelo
  aparece no painel de detalhes.
- **Bancos de sistema** (`master`, `model`, `msdb`, `tempdb`) **não** são submetidos a
  reparo destrutivo automático.
- **Sempre via `master`:** a ferramenta nunca se conecta diretamente ao banco quebrado.
- **Backup de segurança** é tentado antes do reparo com perda de dados (quando possível).
- **Sem mensagens técnicas cruas:** erros viram mensagens compreensíveis; nada interrompe
  a aplicação de forma abrupta.

---

## 12. Boas práticas

1. **Tenha backups.** A restauração de backup é o caminho mais seguro para corrupção de
   dados — e a única forma de recuperar 100% dos dados.
2. **Sempre diagnostique antes de reparar.** Deixe a ferramenta indicar o caminho certo.
3. **Prefira o caminho sem perda.** Só recorra ao reparo com perda de dados se não houver
   backup.
4. **Para reconstrução de log, rode como Administrador** e diretamente no servidor.
5. **Guarde o relatório** (Exportar relatório) ao abrir um chamado.
6. Em ambientes de **alta disponibilidade**, trate a topologia antes de qualquer reparo
   destrutivo.

---

## 13. Solução de problemas (FAQ)

**Não consigo conectar / “Login failed”.**
Verifique o nome do servidor e as credenciais. Para autenticação do Windows, marque a
caixa correspondente. Confirme que a conta tem acesso à instância.

**Conectou, mas avisa que “não é sysadmin”.**
Os reparos exigem `sysadmin`. Conecte-se com um login adequado (a maioria das operações
falhará sem ele).

**As opções de reparo não aparecem.**
Elas só são liberadas **após o diagnóstico**. Selecione o banco e clique em
**“Diagnosticar (sem alterar o banco)”**.

**Aparece um aviso de topologia (espelhamento/AG/replicação).**
O reparo destrutivo foi bloqueado por segurança. Trate a topologia primeiro (failover,
remover do AG/espelhamento, desativar log shipping/replicação) e tente novamente.

**A reconstrução de log diz que exige Administrador.**
Feche e reabra a ferramenta **como Administrador**, no próprio servidor. Como alternativa,
use o **reparo com perda de dados**, que reconstrói o log sem mexer em arquivos. O banco
não é alterado quando a operação é abortada por falta de permissão.

**O diagnóstico mostra páginas suspeitas antigas, mas o CHECKDB está limpo.**
A ferramenta reconcilia o histórico com o estado atual: se o `DBCC CHECKDB` ao vivo está
íntegro, trata-se de corrupção **já resolvida** (sem falso positivo).

**A conexão funciona, mas aparece um aviso de “TLS 1.0”.**
É apenas um aviso por compatibilidade com o SQL Server 2008 R2 (TLS antigo). A conexão é
estabelecida normalmente.

---

## 14. Glossário rápido

- **`DBCC CHECKDB`** — comando que verifica a consistência de um banco.
- **`REPAIR_REBUILD`** — reparo **sem** perda de dados (índices/estruturas reconstruíveis).
- **`REPAIR_ALLOW_DATA_LOSS`** — reparo que pode **descartar** dados corrompidos.
- **Índice não-clusterizado** — estrutura auxiliar e redundante; pode ser reconstruída sem
  perder dados.
- **`.mdf`/`.ndf`** — arquivos de **dados** do banco. **`.ldf`** — arquivo de **log** de
  transações.
- **Usuário órfão** — usuário do banco sem login correspondente no servidor (corrigido
  automaticamente após o restore).
- **Topologia de alta disponibilidade** — espelhamento, Always On (AG), log shipping ou
  replicação.

---

> Para detalhes técnicos (arquitetura, T-SQL exato e cenários de teste), consulte o
> [`README.md`](../README.md) e os scripts em [`sql/`](../sql).
