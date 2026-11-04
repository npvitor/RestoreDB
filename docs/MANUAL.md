# Manual de Utilização — RestoreDB

**RestoreDB — Diagnóstico e reparo de bancos SQL Server**

Este manual é para quem atende o cliente: explica, em linguagem simples, como usar a ferramenta em
cada situação, o que cada botão faz e qual o risco. Os nomes entre aspas são exatamente os que
aparecem na tela.

> **Regra de ouro:** comece sempre pelo **Diagnosticar** (não altera nada) e siga as opções **de
> cima para baixo** na aba **Resolver** (Passo 2) — elas estão em ordem de risco. Se ficar em dúvida diante de uma
> opção vermelha, **pare e chame o DBA**: esperar não piora o banco; uma decisão errada pode piorar.

---

## Sumário

1. [Antes de começar](#1-antes-de-começar)
2. [Conectar ao servidor](#2-conectar-ao-servidor)
3. [A tela principal](#3-a-tela-principal)
4. [O que significam os estados](#4-o-que-significam-os-estados)
5. [O fluxo em três passos](#5-o-fluxo-em-três-passos)
6. [As opções do Passo 2, uma a uma](#6-as-opções-do-passo-2-uma-a-uma)
7. [Situações comuns — o que fazer](#7-situações-comuns--o-que-fazer)
8. [Ferramentas adicionais](#8-ferramentas-adicionais)
9. [Anexar um banco a partir do .mdf](#9-anexar-um-banco-a-partir-do-mdf)
10. [Registro, relatório e cancelamento](#10-registro-relatório-e-cancelamento)
11. [Verificação agendada (monitoramento)](#11-verificação-agendada-monitoramento)
12. [Quando algo dá errado](#12-quando-algo-dá-errado)
13. [Limites — o que a ferramenta não faz](#13-limites--o-que-a-ferramenta-não-faz)
14. [Onde ficam os arquivos](#14-onde-ficam-os-arquivos)
15. [Glossário](#15-glossário)

---

## 1. Antes de começar

Você precisa de:

- **Windows 10 ou 11.** Use a pasta publicada do RestoreDB inteira (o `RestoreDB.App.exe` e as
  DLLs que ficam ao lado dele).
- **SQL Server 2008 R2 até 2025** (inclusive Express).
- **Um login sysadmin** no SQL Server (ex.: `sa` ou um usuário do Windows administrador da
  instância). Sem sysadmin, a ferramenta avisa logo ao conectar e os reparos vão falhar.
- **Para duas opções específicas** — *Corrigir acesso aos arquivos e religar* (quando falta
  permissão na pasta) e *Reconstruir log (recriar .ldf)* —, rodar o RestoreDB **no próprio
  servidor** do SQL Server, com **botão direito → Executar como administrador**. Sem isso a
  ferramenta não altera nada e mostra no registro os comandos para o suporte.
- **Espaço em disco** no servidor: algumas opções fazem backups de segurança e cópias do banco.

Antes de qualquer operação que mexa no banco, **avise o cliente**: quem estiver usando o sistema é
desconectado durante reparos e restaurações.

## 2. Conectar ao servidor

1. Em **SERVIDOR**, digite o nome da instância. Exemplos: `localhost`, `.\SQLEXPRESS`,
   `NOMEDOSERVIDOR\INSTANCIA`, `192.168.0.10,1433`.
2. Escolha a autenticação:
   - marque **"Usar autenticação do Windows (Integrated Security)"** para entrar com o usuário do
     Windows; ou
   - deixe desmarcado e preencha **USUÁRIO** e **SENHA** (login do SQL Server).
3. Clique em **"Conectar"**.

Se aparecer **"RestoreDB — Aviso de pré-requisito"**, leia: normalmente é porque o login não é
sysadmin. Responda **Não** e conecte com outro login. A senha não é guardada; ela é pedida de novo
a cada abertura.

## 3. A tela principal

- **Topo:** servidor conectado, versão do SQL Server e se o login é sysadmin. Botões
  **"⟳ Atualizar lista"**, **"📎 Anexar .mdf"**, **"Trocar conexão"** e **"🌙 Tema escuro"** /
  **"☀ Tema claro"**.
- **Bancos de dados (esquerda):** a lista de bancos com o **Estado**. Use **"🔍 Filtrar por
  nome..."** para achar um banco. A legenda fica embaixo da lista (passe o mouse para ver a
  explicação):
  - **Problema** (linha vermelha) = estado problemático (fora do ar ou em recuperação).
  - **Travado** (linha amarela) = `ONLINE`, mas com acesso restrito ou somente leitura.
  - Um banco sem cor pode, mesmo assim, ter corrupção — **só o diagnóstico mostra**.
- **Cabeçalho do banco (direita, em cima):** o nome do banco, o **estado**, a situação em uma frase
  e a faixa **"PRÓXIMO PASSO"**, que diz o que fazer agora. Aparece igual em todas as abas.
- **Abas:** logo abaixo do cabeçalho ficam as abas **"1 Diagnosticar"**, **"2 Resolver"** e
  **"3 Conferir"**, ligadas por uma linha, e, separada à direita, **"Ferramentas adicionais"**. Só o
  conteúdo da aba escolhida aparece embaixo. Funcionam como um rastreio de entrega:
  - círculo **verde com ✔** = passo concluído (a linha até o próximo também fica verde);
  - círculo **azul** = o passo em que você está;
  - aba **apagada** = ainda bloqueada. Passe o mouse para ver quando ela é liberada.

  As abas do fluxo são liberadas uma de cada vez (veja a seção 5). **"Ferramentas adicionais"**
  fica liberada sempre que houver um banco selecionado. Você pode voltar a uma aba já liberada a
  qualquer momento — por exemplo, para reler o diagnóstico.
- **Resultado da última operação:** uma faixa acima do conteúdo das abas, com ✔ (verde, deu certo),
  ⚠ (amarelo, atenção) ou ✖ (vermelho, falhou). O **✕** à direita fecha a faixa; ela também some
  quando um novo diagnóstico começa.
- **Registro de operações (embaixo):** cada comando executado, em tempo real.
- **Barra de status:** o que está acontecendo, o botão **"■ Cancelar operação"** (só durante uma
  operação) e o nome do arquivo de log — clique nele (ou use Tab + Enter) para abrir a pasta do log.

## 4. O que significam os estados

| Estado | O que quer dizer | Gravidade |
|---|---|---|
| `ONLINE` | No ar. Pode ainda assim ter corrupção escondida. | Normal |
| `OFFLINE` | Desligado de propósito ou por alguém. Os dados estão lá. | Baixa |
| `RESTORING` | Uma restauração ficou pela metade, esperando a finalização. | Média |
| `RECOVERING` | O SQL Server está recuperando o banco sozinho (depois de reiniciar). **Aguarde.** | Média |
| `RECOVERY_PENDING` | A recuperação não conseguiu começar: arquivo faltando, sem permissão, disco cheio, log cheio. | Alta |
| `SUSPECT` | A recuperação começou e falhou: em geral há corrupção. | Alta |
| `EMERGENCY` | Modo de emergência (somente leitura, só sysadmin). Usado durante reparos. | Alta |

Na lista também aparecem situações de **acesso**: `SINGLE_USER` (só uma conexão por vez — o
sistema fica de fora), `RESTRICTED_USER` (só administradores) e somente leitura.

## 5. O fluxo em três passos

Cada passo tem a sua aba. A ferramenta libera a próxima aba quando faz sentido e **muda de aba
sozinha** ao fim de cada etapa. Ao trocar de banco na lista, o fluxo recomeça na aba 1. Durante uma
operação, não dá para trocar de aba nem de banco — espere terminar ou use **"■ Cancelar operação"**.

### Passo 1 — Diagnosticar

1. Selecione o banco na lista. A aba **"1 Diagnosticar"** abre.
2. Clique em **"🔍 Diagnosticar (sem alterar o banco)"**. Em bancos muito grandes, você pode marcar
   **"Verificação rápida (PHYSICAL_ONLY) — para bancos muito grandes"**: é mais rápida, mas faz só a
   checagem física das páginas (sem as checagens lógicas do `CHECKDB` e sem conferir os valores das
   colunas). A checagem de registros órfãos roda mesmo assim.
3. Acompanhe no **Registro de operações**. Bancos grandes podem levar muitos minutos.
4. Ao terminar, a ferramenta marca ✔ no Passo 1 e **abre a aba "2 Resolver"**. O resultado continua
   na aba **"1 Diagnosticar"** — clique nela para ler o **"RESULTADO DO DIAGNÓSTICO"**: o resumo, a
   ação recomendada (linha que começa com `→` ou `✔`) e a explicação. Se houver **"⚠ NOVO desde a
   última verificação"**, é algo que não existia no diagnóstico anterior. **"POLÍTICA DE BACKUP /
   SAÚDE LÓGICA"** lista riscos de backup e sinais de problema de hardware.

O diagnóstico não altera o banco. A única coisa que ele limpa é o histórico antigo de páginas
suspeitas do `msdb`, quando confirma que aquela corrupção já foi resolvida.

Se aparecer **"⚠ TOPOLOGIA DE ALTA DISPONIBILIDADE / REPLICAÇÃO"**, o banco participa de
espelhamento, Always On, log shipping ou replicação: os reparos ficam bloqueados. Nesse caso a
ferramenta **fica na aba 1** para você ler o aviso. **Chame o DBA.**

Se o diagnóstico **não terminar** (erro ou cancelamento), a ferramenta não mostra correções e fica
na aba 1. Veja o motivo no registro e clique em **"Diagnosticar novamente (sem alterar o banco)"**.

### Passo 2 — Resolver

A aba **"2 Resolver"** é liberada depois do diagnóstico. Só aparecem as opções que se aplicam. Se
não houver nada a corrigir, ela mostra **"✔ Nada a corrigir neste banco."** e o fluxo já fica todo
concluído. Cada cartão tem uma etiqueta de risco:

- **SEM PERDA DE DADOS** (verde) — pode usar.
- **MOVE REGISTROS PARA A QUARENTENA** (amarelo) — tira linhas da tabela, mas guarda.
- **RESTAURA DO BACKUP — SUBSTITUI O BANCO** (azul) — volta o banco a um backup.
- **PODE PERDER DADOS** e **⛔ ÚLTIMO RECURSO** (vermelho) — exigem marcar uma caixa de
  confirmação e responder Sim a uma pergunta.

Siga a faixa **"PRÓXIMO PASSO"**: ela diz o nome do cartão por onde começar.

### Passo 3 — Conferir

Depois de uma correção que deu certo, a ferramenta marca ✔ nos passos 1 e 2 e **abre a aba
"3 Conferir"**. Clique em **"🔍 Diagnosticar novamente (sem alterar o banco)"** ali mesmo: só o novo
diagnóstico confirma que o banco ficou íntegro.

- Se estiver tudo certo, a aba continua aberta e mostra **"✔ Conferido"**, com os três passos em
  verde.
- Se aparecer outro problema, a ferramenta volta para a aba **"2 Resolver"** com as novas opções.
- Se a **verificação rápida** estiver marcada na aba 1, a conferência também será parcial: a aba
  avisa em amarelo. Para a conferência completa, desmarque a verificação rápida e diagnostique de
  novo.
- Se a correção deixou uma cópia de salvamento com linhas a devolver, a ferramenta abre a aba
  **"2 Resolver"** no **Assistente de salvamento** em vez da aba Conferir.

A aba Conferir também é liberada quando o primeiro diagnóstico já não encontra nada a corrigir.

## 6. As opções do Passo 2, uma a uma

### Acesso ao banco travado — SEM PERDA

Quando aparece: o banco está `ONLINE`, mas em `SINGLE_USER` ou `RESTRICTED_USER` — o sistema não
consegue entrar. Costuma sobrar de um reparo, restauração ou manutenção interrompidos.

- O cartão mostra **quem está segurando o acesso** (programa, máquina, login).
- **"Desconectar as sessões presas, se preciso"** vem marcado. O que essas sessões ainda não tinham
  gravado é desfeito pelo SQL Server; o que já estava gravado fica.
- Clique em **"Liberar acesso (voltar ao modo normal)"**. Se houver sessões presas, a ferramenta
  pede confirmação listando quem será desconectado.

### Reconstruir índices — SEM PERDA

Quando aparece: a corrupção está só em **índices** (estruturas de busca), não nas tabelas.

- Clique em **"Reparar índices (sem perda de dados)"**. Os índices são refeitos a partir dos dados.
- Quem estiver usando o banco é desconectado durante a operação (não há pergunta de confirmação).
- Se não bastar, aparece o **Reparo no modo seguro** como próximo passo.

### Log de transações cheio — SEM PERDA

Quando aparece: erro 9002 — o arquivo de log não tem mais espaço e o banco recusa gravações. **Não
é corrupção.**

- Clique em **"Resolver log cheio (sem perda de dados)"**. A ferramenta descobre o que segura o log
  e, conforme o caso: faz backup de log, libera o crescimento do arquivo, ou cria um arquivo de log
  extra no disco com mais espaço. Se o banco estava fora do ar por isso, religa.
- **Ela não desconecta ninguém.** Se uma transação aberta estiver segurando o log, o registro mostra
  qual é — combine com o cliente fechar o programa ou chame o DBA.

### Arquivos do banco inacessíveis — SEM PERDA

Quando aparece: o SQL Server não consegue abrir um arquivo do banco (`.mdf`, `.ndf` ou `.ldf`):
arquivo **ausente** (disco ou letra trocados, pasta movida), **sem permissão**, ou **preso** por
outro programa (antivírus, backup de arquivos). **Não é corrupção.**

1. Se algum arquivo estiver **ausente**, clique em **"📁 Localizar arquivo ausente…"** e aponte onde
   ele está agora. O caminho precisa ser válido **no servidor**.
2. Se estiver **preso**, feche o programa que o segura (antivírus, cópia em andamento).
3. Clique em **"Corrigir acesso aos arquivos e religar"**. A ferramenta atualiza o caminho, dá
   permissão à conta do SQL Server (só se estiver rodando como Administrador no servidor) e religa
   o banco.

### Reparo no modo seguro — SEM PERDA

Quando aparece: banco fora do ar (`OFFLINE`, `RESTORING`, `RECOVERY_PENDING`, `SUSPECT`) ou
corrupção que o próprio SQL Server diz que dá para reconstruir sem perda.

- Clique em **"Reparar (modo seguro, sem perda de dados)"**. A ferramenta tenta religar o banco e,
  se preciso, roda o reparo do SQL Server que **só reconstrói estruturas** (`REPAIR_REBUILD`).
- Quem estiver usando o banco é desconectado durante o reparo (não há pergunta de confirmação).
- **Se não bastar, nada é descartado:** a ferramenta para e mostra as próximas opções (restaurar
  de backup e reparo com perda), que pedem confirmação.
- **"Ver script planejado (não executa nada)"** mostra os comandos que o fluxo de reparo usaria
  para o estado atual do banco — útil para mandar ao DBA antes.

Se o estado for `RECOVERING`, a recomendação é **aguardar**: o SQL Server está trabalhando. Use o
reparo só se o andamento não mudar por muito tempo.

### Ajustes recomendados — SEM PERDA

Quando aparece: em qualquer diagnóstico (até num banco íntegro) que encontre configurações que
causam lentidão, travamentos ou escondem corrupção. **Nenhum ajuste altera os dados.**

- Cada item tem a explicação e, embaixo, **"→ Correção: …"**. Marque os que quer aplicar (os
  corrigíveis já vêm marcados, menos o de somente leitura) e clique em **"Aplicar os ajustes
  selecionados"**. A ferramenta pede confirmação.
- Itens com **ℹ** e o texto "Apenas aviso — depende do fornecedor do sistema ou do DBA." não têm
  correção automática (ex.: nível de compatibilidade antigo, `TRUSTWORTHY` ligado).

| Ajuste | Correção |
|---|---|
| Proteção contra corrupção desligada (`PAGE_VERIFY`) | Ligar `PAGE_VERIFY CHECKSUM` |
| Encolhimento automático ligado (`AUTO_SHRINK`) | Desligar `AUTO_SHRINK` |
| Fechamento automático ligado (`AUTO_CLOSE`) | Desligar `AUTO_CLOSE` |
| Crescimento dos arquivos mal configurado | Usar crescimento fixo |
| Log de transações muito fragmentado (VLFs) | Reorganizar o log |
| Usuários do banco sem login correspondente | Religar usuários aos logins |
| Dono do banco inválido | Corrigir o dono do banco |
| Banco em modo SOMENTE LEITURA | Liberar gravação (`READ_WRITE`) — **vem desmarcado**: pode ser proposital |
| Regras de integridade não verificadas (constraints não confiáveis) | Revalidar as regras |
| Índices desabilitados | Reconstruir os índices |
| Views/procedures com referência quebrada | Revalidar os objetos |

Alguns ajustes (reconstruir índices, reorganizar o log) podem demorar em bancos grandes. Se houver
outra correção na tela, ela continua pendente depois dos ajustes.

### Integridade lógica: registros órfãos — QUARENTENA

Quando aparece: há linhas cuja **chave estrangeira aponta para um registro que não existe** (ex.:
um item de nota cuja nota foi apagada). O `DBCC CHECKDB` não detecta isso.

1. (Opcional) Em **"BACKUP PARA RECUPERAR REGISTROS-PAI (OPCIONAL)"**: por padrão
   (**"↺ Automático"**) a ferramenta usa o backup completo mais recente do histórico. Se o backup
   estiver em outra mídia, clique em **"📁 Escolher .bak…"** (o SQL Server precisa conseguir ler o
   arquivo).
2. Marque **"Entendo e autorizo retirar os registros órfãos das tabelas (ficam guardados na
   quarentena)."**
3. Clique em **"Corrigir integridade (órfãos para a quarentena)"** e confirme.

O que acontece: a ferramenta tenta um backup de segurança; procura no backup os **registros-pai**
que faltam e os devolve; se não achar todos, pergunta se você quer escolher **outro .bak**; os órfãos
que sobrarem **saem da tabela e ficam guardados no banco `RestoreDB_Quarentena`**, de onde o DBA
pode devolvê-los. Se a quarentena não puder ser usada numa tabela, as linhas dessa tabela são
**excluídas** (o registro avisa). Quem estiver usando o banco é desconectado.

Se o problema for só de regra `CHECK` ou de catálogo, **não há correção automática**: a faixa
"PRÓXIMO PASSO" pede para revisar os dados com o fornecedor/DBA ou restaurar de backup.

### Restaurar de backup — SUBSTITUI O BANCO

Quando aparece: corrupção nos arquivos de **dados**, registros órfãos (como alternativa) ou quando o
reparo sem perda não bastou. Restaurar traz dados **íntegros** do backup, em vez de descartar o que
está corrompido.

**Caminho recomendado — "Restaurar cadeia completa (mais recente)":**
a ferramenta junta sozinha o backup completo, o diferencial e os backups de log do histórico, confere
que a sequência não tem buracos (se tiver, não mexe no banco), tenta salvar as transações mais
recentes (**tail-log**) e então substitui o banco. Se o tail-log não puder ser salvo (modelo
`SIMPLE` ou log danificado), **o que foi gravado depois do último backup se perde**. Pede
confirmação. Quem estiver usando o banco é desconectado.

**"OU ESCOLHA UM BACKUP ESPECÍFICO":**

1. A lista mostra os backups do histórico do servidor. Para usar um arquivo, clique em
   **"📁 Escolher arquivo .bak..."**.
2. Selecione um backup na lista.
3. **"Substituir o banco existente (REPLACE)"** vem marcado (sem ele, a restauração falha se o banco
   já existir).
4. Clique em **"Restaurar o backup selecionado"**. O banco volta **exatamente ao momento desse
   backup**: tudo o que foi gravado depois **se perde** (este modo não salva o tail-log). Se o
   backup escolhido não for completo (é diferencial ou de log), a ferramenta avisa — prefira a
   cadeia completa.

**"Mais opções de restauração (avançado)":**

- *SEM ALTERAR O BANCO*
  - **"Testar backup selecionado (restauração de teste)"** — restaura um backup completo num banco
    temporário, verifica e apaga. Prova que o backup funciona.
  - **"Verificar saúde dos backups (VERIFYONLY)"** — confere se os arquivos de backup do histórico
    são legíveis, sem restaurar.
  - **"Recuperar para cópia (salvamento, não altera o original)"** — restaura o backup mais recente
    num banco novo `<banco>_Recuperado`. Depois aparece o **Assistente de salvamento** (abaixo).
- *PONTO NO TEMPO / INSPEÇÃO*
  - **"Restaurar até um ponto no tempo (STOPAT)"** — marque e informe data e hora: vale para a
    cadeia completa e para o STANDBY. O que foi gravado depois desse instante deixa de existir no
    banco (fica só no arquivo de tail-log). Exige modelo `FULL` e a cadeia de backups de log.
  - **"Restaurar em STANDBY (substitui o banco; fica só leitura para inspecionar)"** — o banco
    fica consultável, mas o sistema não grava nele. **Não salva o tail-log.** Para só olhar dados,
    prefira "Recuperar para cópia".
  - **"Finalizar recuperação (sair do STANDBY)"** — devolve o banco ao uso normal.
- *SÓ AS PÁGINAS AFETADAS*
  - Quando poucas páginas estão corrompidas, recupera só elas e preserva o resto. O campo já vem
    preenchido pelo diagnóstico (formato `arquivo:página`, ex.: `1:57,1:58`). Clique em
    **"Restaurar apenas estas páginas"**. Exige modelo `FULL`/`BULK_LOGGED`, banco `ONLINE` e a
    cadeia de backups.

### Assistente de salvamento — SEM PERDA

Aparece depois de "Recuperar para cópia", de um reparo com perda que guardou linhas para revisão, ou
de uma recuperação de exclusão que não conseguiu devolver tudo.

1. Escolha a tabela na lista.
2. Clique em **"Copiar linhas faltantes desta tabela"** e confirme.

Só entram no banco as linhas que **ainda não existem** (pela chave primária) — nada é duplicado.
Tabelas sem chave primária e linhas parciais (`tabela__parcial`) precisam do DBA.

### Reparo com possível perda de dados — PODE PERDER DADOS

Aparece quando o **reparo sem perda não bastou**. A faixa "PRÓXIMO PASSO" lembra: **se houver
backup, prefira "Restaurar de backup".** Use o reparo com perda quando não houver backup bom.

1. Clique em **"🔬 Simular antes (pré-visualizar a perda, sem tocar no banco)"**. A ferramenta faz
   uma cópia do banco, repara a cópia e mostra quantas linhas cada tabela perderia. A cópia é
   apagada ao final. Precisa de espaço em disco para a cópia.
2. (Opcional) Escolha o backup para recuperar registros-pai, como no cartão de órfãos.
3. Deixe marcado **"Salvar e devolver automaticamente as linhas que o reparo descartar
   (recomendado)"**: antes do reparo, as linhas que ainda dá para ler nas tabelas danificadas são
   copiadas para um banco de salvamento; depois do reparo, as que foram descartadas voltam. Se a
   cadeia de backups chegar até o instante do reparo, ela também é usada.
4. Marque **"Estou ciente e autorizo o reparo com possível perda de dados."**
5. Clique em **"Executar reparo COM perda de dados"** e confirme.

O que acontece, em ordem: tail-log e backup de segurança → cópia das linhas legíveis →
`REPAIR_ALLOW_DATA_LOSS` → verificação → devolução das linhas descartadas → tratamento de órfãos
(recupera o pai ou move para a quarentena) → reajuste das identidades → **backup completo novo** se
o banco ficou íntegro.

> **O que pode se perder:** linhas que estavam em páginas **destruídas** e que não estão em nenhum
> backup. O salvamento só copia o que ainda dá para ler. O resultado diz, em linguagem simples, o
> que ficou de fora; se uma cópia de salvamento for mantida, use o Assistente de salvamento ou
> chame o DBA.

### Reconstruir o log de transações — ⛔ ÚLTIMO RECURSO

Aparece quando o problema está no **log de transações** (`.ldf` corrompido ou ausente, com o banco
em `RECOVERY_PENDING`/`SUSPECT`) e o reparo não resolveu. Nunca aparece para bancos de sistema.

- Exige rodar o RestoreDB **como Administrador no próprio servidor**. Sem isso, a ferramenta
  **não altera nada** e mostra as instruções para o DBA.
- Marque **"Entendo os riscos e autorizo reconstruir o log."** e clique em
  **"Reconstruir log (recriar .ldf)"**, confirmando.
- A ferramenta tenta copiar os arquivos para o lado, desanexa o banco, guarda o log antigo com a
  extensão `.corrupt_AAAAMMDDHHMMSS`, cria um log novo e verifica o banco.
- **Pode haver perda de dados** (o que estava só no log) e inconsistências. Depois, faça o Passo 3.

### Bancos de sistema (`master`, `model`, `msdb`, `tempdb`)

A ferramenta, no máximo, tenta **religar** um banco de sistema (modo seguro). Corrupção e
restauração são manuais: clique em **"Ver procedimento manual"** e acione o DBA.

## 7. Situações comuns — o que fazer

| O cliente diz… / você vê… | Faça |
|---|---|
| "O sistema não abre o banco" e a linha está **amarela** (`SINGLE_USER`) | Diagnosticar → **Acesso ao banco travado** → "Liberar acesso (voltar ao modo normal)". |
| "Não consigo gravar nada" (somente leitura) | Diagnosticar → **Ajustes recomendados** → marque "Banco em modo SOMENTE LEITURA" → "Aplicar os ajustes selecionados". |
| Erro 9002 / "log de transações cheio" | Diagnosticar → **Log de transações cheio** → "Resolver log cheio (sem perda de dados)". |
| Banco `RECOVERY_PENDING` depois de trocar disco ou letra | Diagnosticar → **Arquivos do banco inacessíveis** → "📁 Localizar arquivo ausente…" → "Corrigir acesso aos arquivos e religar". |
| Banco `OFFLINE` ou `RESTORING` | Diagnosticar → **Reparo no modo seguro**. |
| Banco `RECOVERING` | Aguarde e diagnostique de novo mais tarde. |
| Banco `SUSPECT` | Diagnosticar → siga a recomendação. Com backup, **Restaurar de backup**. Sem backup: **Reparo no modo seguro** → se não bastar, **Simular antes** → reparo com perda (só com autorização do responsável). |
| Banco `ONLINE`, mas erros 823/824 ou "página corrompida" | Diagnosticar. Só índices → **Reconstruir índices**. Dados → **Restaurar de backup** (ou só as páginas). |
| Registros "sumidos" ligados a outros (órfãos) | Diagnosticar → **Integridade lógica: registros órfãos**. |
| "Apagaram dados sem querer" | **Ferramentas adicionais → Recuperar exclusão acidental** (seção 8.4). |
| Banco grande demais para diagnosticar no horário disponível | **Ferramentas adicionais → Verificação em partes**. |
| Só tenho o `.mdf` | **"📎 Anexar .mdf"** (seção 9). |

## 8. Ferramentas adicionais

Ficam na aba **"Ferramentas adicionais"**, à direita das abas do fluxo, liberada sempre que houver
um banco selecionado — não depende do diagnóstico. Dentro dela, a lista de ferramentas fica à
esquerda e a ferramenta escolhida à direita (em janelas estreitas, o detalhe desce para baixo da
lista). São independentes do Passo 2: usar uma delas não conclui o fluxo de correção.

Quando uma ferramenta altera o banco (por exemplo, devolver linhas excluídas), você **continua nesta
aba** e os círculos dos passos se atualizam. A exceção é quando sobra uma cópia com linhas a
devolver: aí a ferramenta abre a aba **"2 Resolver"** no **Assistente de salvamento**.

### 8.1. Verificar uma tabela

- Escolha ou digite a tabela (ex.: `dbo.Pedido`) e clique em **"Verificar tabela"**: roda
  `DBCC CHECKTABLE` só nela — bem mais rápido que o diagnóstico completo. Somente leitura.
- **"Reajustar identidades"** (`CHECKIDENT RESEED`): use depois de um reparo com perda ou de um
  salvamento, quando o próximo código gerado pelo sistema colide com um que já existe. Não pede
  confirmação; não altera linhas.

### 8.2. Manutenção preventiva

Nenhuma destas ações apaga dados.

- **"Otimizar"** — desfragmenta índices (`REORGANIZE` de 10% a 30% de fragmentação, `REBUILD` acima
  de 30%) e atualiza estatísticas. Pede confirmação. Pode demorar e pesar no servidor: prefira fora
  do horário de uso.
- **"Corrigir contadores"** (`DBCC UPDATEUSAGE`) — quando tamanhos ou quantidades de linhas aparecem
  errados (comum em bancos vindos do SQL 2000/2005). Só ajusta o catálogo.
- **"Gerar comando"** — mostra o comando para a verificação agendada (seção 11). Não executa nada.

### 8.3. Verificação em partes

Para bancos grandes demais para o diagnóstico completo na janela de manutenção.

1. Em **"Tempo desta rodada"**, escolha 15 minutos, 30 minutos, 1 hora ou 2 horas.
2. Clique em **"Verificar próxima parte"**. A ferramenta verifica as tabelas uma a uma até acabar o
   tempo e guarda onde parou. **"Andamento"** mostra em que tabela está o ciclo.
3. Repita em outros dias. Ao fim do ciclo, o banco inteiro foi verificado.

Somente leitura. Se aparecerem problemas, rode o **Diagnosticar** completo para ver as opções.
Cancelar no meio não perde o progresso.

### 8.4. Recuperar exclusão acidental

1. Clique em **"Procurar no log"**: a ferramenta lê o log de transações e lista os `DELETE`, `DROP`
   e `TRUNCATE` recentes (somente leitura). Se o log já foi reaproveitado, a exclusão pode não
   aparecer mais.
2. Selecione a transação na lista e escolha:
   - **"Devolver só as linhas excluídas (não sobrescreve o banco)"** — *preferível*. Uma cópia
     temporária é restaurada até logo antes da exclusão e só as linhas que faltam voltam (por chave
     primária). Tudo o que foi feito depois continua no banco. Atenção: linhas daquela tabela
     excluídas **de propósito** depois desse instante também voltariam. Serve para `DELETE` numa
     tabela identificada.
   - **"Restaurar o banco inteiro até ANTES da transação"** — substitui o banco pela cadeia de
     backups até um segundo antes. **Tudo o que foi gravado depois deixa de existir no banco**
     (fica só no arquivo de tail-log). Exige modelo `FULL` e a cadeia de backups. Use para
     `DROP`/`TRUNCATE`.

## 9. Anexar um banco a partir do .mdf

Quando o banco não está no servidor, mas você tem o arquivo de dados (`.mdf`).

1. Clique em **"📎 Anexar .mdf"** e escolha o arquivo. O nome do banco será o nome do arquivo.
2. A ferramenta procura o `.ldf` ao lado. Se não achar, pergunta se você quer localizá-lo. Se não
   houver log, **um log novo é criado** — transações que não estavam confirmadas podem se perder.
3. Confirme. O banco aparece na lista. Não sobrescreve um banco que já exista com o mesmo nome.

Depois, diagnostique o banco anexado.

## 10. Registro, relatório e cancelamento

- **Registro de operações:** tudo o que a ferramenta faz, com horário. Cores indicam informação,
  sucesso, aviso e erro. **"Limpar"** só limpa a tela (o arquivo de log continua).
- **"Exportar relatório":** salva o registro da sessão num arquivo HTML — anexe ao chamado.
- **"■ Cancelar operação":** interrompe a operação em andamento. A ferramenta tenta deixar o banco
  em modo normal ao parar. Espere a mensagem "Operação cancelada." antes de fazer outra coisa.
  Enquanto uma operação roda, as abas e a lista de bancos ficam bloqueadas.
- **Não feche a janela no meio de uma operação.** A ferramenta pergunta antes; fechar pode deixar o
  banco travado em `SINGLE_USER` ou num estado intermediário. Se isso acontecer, diagnostique de
  novo e use **Acesso ao banco travado**.

## 11. Verificação agendada (monitoramento)

Para descobrir problemas cedo, sem esperar o cliente ligar.

1. Em **Ferramentas adicionais → Manutenção preventiva → Verificação agendada**, clique em
   **"Gerar comando"**. Aparece um comando `schtasks` para o Agendador de Tarefas do Windows.
2. Peça ao responsável pela máquina para registrar a tarefa, trocando `C:\Caminho\` pelo local do
   `RestoreDB.Probe.exe` (o utilitário de linha de comando do RestoreDB — veja o README).
3. A conta que roda a tarefa precisa ser sysadmin no SQL Server (o comando usa a autenticação do
   Windows).

Todo dia a tarefa faz um diagnóstico **somente leitura** e compara com o anterior. Quando aparece
algo **novo** (corrupção nova, mais erros, mais registros órfãos, banco fora do ar), grava um alerta
em `%LOCALAPPDATA%\RestoreDB\Alerts` e termina com **código de saída 2**. A **primeira execução**
só registra a situação inicial — ela não alerta.

> A ação "Enviar e-mail" citada na tela é antiga e não funciona nas versões atuais do Agendador do
> Windows. Para avisar o suporte por e-mail, use um script que confira o código de saída 2 ou a
> pasta de alertas e envie a mensagem.

## 12. Quando algo dá errado

| O que aconteceu | O que fazer |
|---|---|
| "Falha ao conectar" | Confira o nome da instância, se o serviço do SQL Server está rodando, o firewall e a senha. Para instância nomeada, use `SERVIDOR\INSTANCIA`. |
| Aviso de que o login não é sysadmin | Conecte com um login sysadmin. Sem ele, reparos e restaurações falham. |
| O diagnóstico não terminou | Leia o motivo no registro. Banco inacessível ou ocupado: tente de novo; se persistir, chame o DBA. |
| "Operação bloqueada por segurança" (topologia) | O banco está em espelhamento/Always On/log shipping/replicação. Chame o DBA. |
| O reparo seguro terminou com "não foi suficiente" | Normal: a ferramenta não descarta nada sozinha. Siga as opções que aparecerem (restaurar ou simular + reparo com perda). |
| Mensagem sobre transações **ADIADAS** (deferred transactions) | O SQL Server não consegue desfazer uma transação por causa de uma página danificada; nem o reparo roda. Restaure as páginas ("Restaurar apenas estas páginas") ou o banco inteiro; sem backup, chame o DBA. |
| A cadeia de backups "tem buracos" ou não existe | A restauração da cadeia não mexe no banco. Escolha um backup específico (aceitando a perda do que veio depois) ou chame o DBA. |
| Restauração de páginas recusada | Exige modelo `FULL`/`BULK_LOGGED`, banco `ONLINE` e a cadeia de backups. Use a restauração do banco inteiro. |
| "Exige executar a ferramenta como Administrador" | Feche o RestoreDB e abra de novo no próprio servidor com **Executar como administrador**. |
| O banco ficou em `SINGLE_USER` depois de um erro ou cancelamento | Diagnosticar → **Acesso ao banco travado** → "Liberar acesso (voltar ao modo normal)". |
| Uma cópia `_Recuperado`, `_Salvamento_` ou `_AntesExclusao_` ficou no servidor | Foi mantida para revisão. Depois de recuperar o que precisa (Assistente de salvamento) e com o DBA de acordo, ela pode ser apagada pelo SQL Server Management Studio. |
| Corrupção que volta depois de reparada | Possível problema de disco/controladora. O diagnóstico avisa ("Corrupção RECORRENTE"). Chame o responsável pelo hardware. |

Em qualquer dúvida: **Exportar relatório** e envie ao DBA junto com o arquivo de log.

## 13. Limites — o que a ferramenta não faz

Seja transparente com o cliente:

- **O reparo com perda pode descartar dados.** O que estava em páginas destruídas e não está em
  nenhum backup **não volta**.
- **O salvamento sem backup não recupera páginas destruídas** — só copia o que ainda dá para ler.
- **Restaurar substitui o banco.** O que foi gravado depois do backup (ou do ponto escolhido) se
  perde quando o tail-log não pode ser salvo.
- **Reconstruir o log** pode deixar o banco com dados inconsistentes.
- **Não corrige** regras `CHECK` violadas nem inconsistências de catálogo — avisa e pede o DBA.
- **Não repara nem restaura bancos de sistema.**
- **Não substitui backup.** Sem uma rotina de backup, várias situações não têm saída sem perda.
- **Não funciona no Azure SQL Database.**

## 14. Onde ficam os arquivos

| Onde | O quê |
|---|---|
| `%LOCALAPPDATA%\RestoreDB\Logs\repair-AAAAMMDD.log` | Log de cada comando (o nome aparece na barra de status; clique nele para abrir a pasta). |
| `%LOCALAPPDATA%\RestoreDB\History\` | Histórico dos diagnósticos (para mostrar o que é novo). |
| `%LOCALAPPDATA%\RestoreDB\IncrementalCheck\` | Progresso da verificação em partes. |
| `%LOCALAPPDATA%\RestoreDB\Alerts\` | Alertas da verificação agendada. |
| Pasta de backups do SQL Server | `_tail_` (tail-log), `_RestoreDB_` (backup de segurança) e `_PosReparo_` (backup novo depois do reparo com perda). |
| Bancos no servidor | `RestoreDB_Quarentena`, `<banco>_Recuperado`, `<banco>_Salvamento_<data>`, `<banco>_AntesExclusao_<data>`. |

Esses arquivos e bancos ocupam espaço: combine com o DBA quando apagá-los.

## 15. Glossário

- **Backup completo (FULL):** cópia do banco inteiro num momento.
- **Backup diferencial:** o que mudou desde o último backup completo.
- **Backup de log:** as transações desde o backup de log anterior. Com eles dá para voltar a
  qualquer momento (ponto no tempo).
- **Cadeia de backups:** completo → diferencial → logs, em sequência e sem buracos.
- **Tail-log:** o último pedaço do log, ainda sem backup. Salvá-lo antes de restaurar evita perder
  as transações mais recentes.
- **Modelo de recuperação:** `SIMPLE` (sem backups de log; não permite ponto no tempo nem tail-log)
  ou `FULL`/`BULK_LOGGED` (com backups de log).
- **DBCC CHECKDB:** verificação de consistência do SQL Server. Com `REPAIR_REBUILD` repara sem
  perda; com `REPAIR_ALLOW_DATA_LOSS`, descarta o que está corrompido.
- **Página:** bloco de 8 KB onde o SQL Server guarda os dados. "Página corrompida" = bloco ilegível.
- **Índice clusterizado / não clusterizado:** o clusterizado é a própria tabela ordenada; os não
  clusterizados são cópias auxiliares para busca — dá para refazê-los sem perder dados.
- **Chave primária:** o código que identifica cada linha (ex.: número da nota).
- **Chave estrangeira:** uma coluna que aponta para a chave primária de outra tabela.
- **Registro órfão:** linha cuja chave estrangeira aponta para um registro que não existe.
- **Quarentena:** o banco `RestoreDB_Quarentena`, onde ficam guardados os órfãos retirados das
  tabelas.
- **SINGLE_USER / MULTI_USER:** só uma conexão por vez / modo normal.
- **STANDBY:** banco restaurado em modo só leitura, à espera de mais backups ou da finalização.
- **Erro 9002:** log de transações cheio.
- **Erros 823/824/825:** erros de leitura no disco — corrupção ou problema de hardware.
- **VLF:** pedaços internos do arquivo de log; em excesso deixam o banco lento para subir.
- **TDE:** criptografia do banco; restaurar em outro servidor exige o certificado do original.
- **sysadmin:** o perfil com poder total no SQL Server.
- **Topologia (espelhamento, Always On, log shipping, replicação):** o banco tem cópias em outros
  servidores; repará-lo sozinho pode quebrar essa configuração.
