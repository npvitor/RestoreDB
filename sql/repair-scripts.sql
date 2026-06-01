/* ============================================================================
   RestoreDB — Scripts T-SQL de reparo (Módulo de Faturamento)
   Compatíveis com SQL Server 2008 R2 (e versões superiores).

   Estes são EXATAMENTE os comandos que a aplicação executa. Na aplicação eles
   são montados com QUOTENAME(@db) e executados via sp_executesql (o nome do
   banco entra como parâmetro, evitando injeção de SQL). Aqui estão na forma
   legível, com o placeholder [NomeDoBanco] para revisão por um DBA.

   AVISO: comandos marcados como DESTRUTIVOS podem causar PERDA DE DADOS.
   ============================================================================ */


/* ----------------------------------------------------------------------------
   0) Listagem de bancos e estados (tela de listagem)
   ---------------------------------------------------------------------------- */
SELECT  d.name                              AS [name],
        CAST(d.state AS int)                AS [state],
        d.state_desc                        AS [state_desc],
        ISNULL(d.recovery_model_desc, N'')  AS [recovery_model_desc]
FROM    sys.databases AS d
ORDER BY CASE WHEN d.state <> 0 THEN 0 ELSE 1 END, d.name;
GO


/* ----------------------------------------------------------------------------
   1) OFFLINE  ->  religar o banco (SEM perda de dados)
   ---------------------------------------------------------------------------- */
ALTER DATABASE [NomeDoBanco] SET ONLINE;
ALTER DATABASE [NomeDoBanco] SET MULTI_USER;
GO


/* ----------------------------------------------------------------------------
   2) RESTORING / RECOVERING  ->  finalizar recuperação (SEM perda de dados)
   ---------------------------------------------------------------------------- */
RESTORE DATABASE [NomeDoBanco] WITH RECOVERY;
GO


/* ----------------------------------------------------------------------------
   3) RECOVERY_PENDING  ->  geralmente falta de arquivo/recurso.
      Primeiro tenta religar; se não bastar, segue para o fluxo 4 (EMERGENCY).
   ---------------------------------------------------------------------------- */
ALTER DATABASE [NomeDoBanco] SET ONLINE;   -- se falhar, usar o fluxo 4
GO


/* ----------------------------------------------------------------------------
   4) SUSPECT / EMERGENCY  ->  diagnóstico + reparo escalonado
      (também usado como fallback do RECOVERY_PENDING)

   IMPORTANTE: o SQL Server NÃO permite REPAIR_REBUILD com o banco em modo
   EMERGENCY (Msg 7901 — "This level of repair is not supported when the
   database is in emergency mode"). Por isso a recuperação normal (SET ONLINE)
   é tentada primeiro: se o banco voltar a ONLINE, o REPAIR_REBUILD passa a ser
   permitido (banco ONLINE + SINGLE_USER), sem perda de dados.
   ---------------------------------------------------------------------------- */

-- 4.1 Tornar o banco acessível para diagnóstico
ALTER DATABASE [NomeDoBanco] SET EMERGENCY;

-- 4.2 Acesso exclusivo (derruba demais conexões)
ALTER DATABASE [NomeDoBanco] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;

-- 4.3 Diagnóstico (somente leitura) — funciona em EMERGENCY; lista as inconsistências
DBCC CHECKDB ([NomeDoBanco]) WITH NO_INFOMSGS, ALL_ERRORMSGS;

-- 4.4 Recuperação normal SEM perda de dados (tira o banco do modo EMERGENCY)
ALTER DATABASE [NomeDoBanco] SET ONLINE;

-- 4.5 Reparo SEM perda de dados (requer o banco ONLINE + SINGLE_USER)
DBCC CHECKDB ([NomeDoBanco], REPAIR_REBUILD) WITH ALL_ERRORMSGS;

-- 4.6 Verificação após o reparo
DBCC CHECKDB ([NomeDoBanco]) WITH NO_INFOMSGS, ALL_ERRORMSGS;

/* --- Se 4.5/4.6 ainda acusarem erros, e SOMENTE com confirmação do usuário: --- */

-- 4.7 (DESTRUTIVO) Backup de segurança antes do reparo com perda de dados.
--     Caminho relativo => grava na pasta padrão de backups da instância.
--     OBS.: falha se o banco estiver em EMERGENCY (Msg 3033) — nesse caso a
--     ferramenta apenas avisa que o backup não foi possível e prossegue.
ALTER DATABASE [NomeDoBanco] SET EMERGENCY;
ALTER DATABASE [NomeDoBanco] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
BACKUP DATABASE [NomeDoBanco]
    TO DISK = N'NomeDoBanco_RestoreDB_AAAAMMDD_HHMMSS.bak'
    WITH COPY_ONLY, INIT, NAME = N'RestoreDB COPY_ONLY backup';

-- 4.8 (DESTRUTIVO) Reparo COM possível PERDA DE DADOS (permitido em EMERGENCY)
DBCC CHECKDB ([NomeDoBanco], REPAIR_ALLOW_DATA_LOSS) WITH ALL_ERRORMSGS;
DBCC CHECKDB ([NomeDoBanco]) WITH NO_INFOMSGS, ALL_ERRORMSGS;

-- 4.9 Retornar o banco ao uso normal (sai de emergency e volta a ONLINE)
ALTER DATABASE [NomeDoBanco] SET MULTI_USER;
GO


/* ----------------------------------------------------------------------------
   5) ÚLTIMO RECURSO — banco irreparável por causa do LOG de transações:
      reconstruir o .ldf (detach -> remover o log -> ATTACH_REBUILD_LOG).

      Use SOMENTE quando os reparos acima falharem. PODE HAVER PERDA DE DADOS
      (transações não confirmadas são descartadas) e o banco pode ficar
      logicamente inconsistente — por isso rode um DBCC CHECKDB ao final.

      Comportamento validado em engine real:
        * sp_detach_db FUNCIONA com o banco em EMERGENCY;
        * ATTACH_REBUILD_LOG só recria o log se o arquivo .ldf NÃO existir
          fisicamente — por isso o passo 5.3 (remover/renomear o .ldf) é
          obrigatório. Com o .ldf presente, o ATTACH falha ("File activation
          failure ... CREATE DATABASE is aborted").
   ---------------------------------------------------------------------------- */

-- 5.1 Descobrir os arquivos físicos (dados e log)
SELECT type_desc, physical_name
FROM   sys.master_files
WHERE  database_id = DB_ID(N'NomeDoBanco')
ORDER BY type, file_id;

-- 5.2 EMERGENCY + detach (libera os arquivos no SO)
ALTER DATABASE [NomeDoBanco] SET EMERGENCY;
EXEC master.dbo.sp_detach_db @dbname = N'NomeDoBanco', @skipchecks = 'true';

-- 5.3 (NO SISTEMA OPERACIONAL) renomear/apagar o(s) arquivo(s) .ldf.
--     Ex.: ren "C:\...\NomeDoBanco_log.ldf" "NomeDoBanco_log.ldf.corrupt"

-- 5.4 Reanexar reconstruindo o log (informe APENAS o(s) arquivo(s) de dados)
CREATE DATABASE [NomeDoBanco] ON
    (FILENAME = N'C:\...\NomeDoBanco.mdf')   -- repita para cada .ndf, se houver
    FOR ATTACH_REBUILD_LOG;

-- 5.5 Normalizar e verificar consistência
ALTER DATABASE [NomeDoBanco] SET MULTI_USER;
DBCC CHECKDB ([NomeDoBanco]) WITH NO_INFOMSGS, ALL_ERRORMSGS;
GO


/* ----------------------------------------------------------------------------
   6) DIAGNÓSTICO ESTRUTURADO (somente leitura) — fontes autoritativas
   ---------------------------------------------------------------------------- */

-- 6.1 Páginas suspeitas registradas pelo SQL Server (erros 823/824/825).
SELECT file_id, page_id, event_type, error_count, last_update_date
FROM   msdb.dbo.suspect_pages
WHERE  database_id = DB_ID(N'NomeDoBanco');
-- event_type: 1 = 823/824; 2 = checksum; 3 = página rasgada; 4 = restaurada; 5 = reparada; 7 = desalocada.

-- 6.2 CHECKDB com saída ESTRUTURADA: identifica objeto/índice e o nível mínimo de reparo,
--     sem precisar parsear texto livre. (index_id > 1 => índice NÃO clusterizado.)
DBCC CHECKDB (N'NomeDoBanco') WITH TABLERESULTS, ALL_ERRORMSGS, NO_INFOMSGS;

-- 6.3 Opção PAGE_VERIFY (recomenda-se CHECKSUM para detectar corrupção futura).
SELECT page_verify_option_desc FROM sys.databases WHERE name = N'NomeDoBanco';
ALTER DATABASE [NomeDoBanco] SET PAGE_VERIFY CHECKSUM;  -- se estiver NONE/TORN_PAGE_DETECTION

-- 6.4 Espaço livre dos volumes do banco (2008 R2 SP1+).
SELECT DISTINCT vs.volume_mount_point, vs.total_bytes, vs.available_bytes
FROM   sys.master_files mf
CROSS APPLY sys.dm_os_volume_stats(mf.database_id, mf.file_id) vs
WHERE  mf.database_id = DB_ID(N'NomeDoBanco');
GO


/* ----------------------------------------------------------------------------
   7) REPARO SEM PERDA DE DADOS — corrupção restrita a índices NÃO clusterizados
      Quando o CHECKDB (6.2) mostra erros apenas em índices com index_id > 1,
      basta reconstruí-los: NENHUM dado é perdido. (Mesmo que o CHECKDB sugira
      'repair_allow_data_loss', um índice não-clusterizado é redundante.)

      IMPORTANTE (validado com corrupção real): primeiro DISABLE, depois REBUILD.
      Um REBUILD direto LÊ as páginas do índice — se elas estiverem fisicamente
      corrompidas (erro 824), o comando falha e derruba a conexão. O DISABLE
      desaloca a estrutura SEM ler as páginas; o REBUILD recria o índice a partir
      do índice clusterizado/heap (íntegro).
   ---------------------------------------------------------------------------- */
ALTER DATABASE [NomeDoBanco] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
ALTER INDEX [NomeDoIndice] ON [schema].[Tabela] DISABLE;   -- não lê as páginas corrompidas
ALTER INDEX [NomeDoIndice] ON [schema].[Tabela] REBUILD;   -- recria a partir dos dados íntegros
DBCC CHECKDB (N'NomeDoBanco') WITH NO_INFOMSGS, ALL_ERRORMSGS;  -- verificação
ALTER DATABASE [NomeDoBanco] SET MULTI_USER;
GO


/* ----------------------------------------------------------------------------
   8) RESTAURAR DE BACKUP — caminho correto para corrupção de DADOS (.mdf/.ndf)
   ---------------------------------------------------------------------------- */

-- 8.1 Inspecionar o arquivo de backup
RESTORE HEADERONLY   FROM DISK = N'C:\Backups\NomeDoBanco.bak';
RESTORE FILELISTONLY FROM DISK = N'C:\Backups\NomeDoBanco.bak' WITH FILE = 1;
RESTORE VERIFYONLY   FROM DISK = N'C:\Backups\NomeDoBanco.bak' WITH FILE = 1;

-- 8.2 Restaurar (substituindo o banco corrompido; realoque arquivos com MOVE se necessário)
RESTORE DATABASE [NomeDoBanco] FROM DISK = N'C:\Backups\NomeDoBanco.bak'
    WITH FILE = 1,
         MOVE N'NomeDoBanco'      TO N'C:\Dados\NomeDoBanco.mdf',
         MOVE N'NomeDoBanco_log'  TO N'C:\Dados\NomeDoBanco_log.ldf',
         REPLACE, RECOVERY;

-- 8.3 RESTAURAÇÃO DE PÁGINA (page restore) — recupera só as páginas corrompidas (poucas).
--     Requer a cadeia full + logs e modelo de recuperação FULL.
RESTORE DATABASE [NomeDoBanco] PAGE = '1:57' FROM DISK = N'C:\Backups\NomeDoBanco.bak'
    WITH FILE = 1, NORECOVERY;
RESTORE LOG [NomeDoBanco] FROM DISK = N'C:\Backups\NomeDoBanco.trn' WITH FILE = 1, RECOVERY;

-- 8.4 Corrigir usuários órfãos após o RESTORE (reassocia ao login de mesmo nome).
--     A aplicação executa este lote NO CONTEXTO do banco restaurado.
-- ALTER USER [usuario] WITH LOGIN = [usuario];
GO


/* ----------------------------------------------------------------------------
   9) DETECÇÃO DE TOPOLOGIA — antes de qualquer reparo destrutivo/restore
      Espelhamento, Always On, log shipping e replicação tornam o reparo
      destrutivo/detach PERIGOSO. A ferramenta BLOQUEIA o reparo nesses casos.
   ---------------------------------------------------------------------------- */
SELECT mirroring_role_desc, mirroring_state_desc
FROM   sys.database_mirroring WHERE database_id = DB_ID(N'NomeDoBanco') AND mirroring_guid IS NOT NULL;

SELECT is_published, is_subscribed, is_merge_published
FROM   sys.databases WHERE name = N'NomeDoBanco';

-- Always On (2012+): sys.availability_databases_cluster / sys.availability_groups
-- Log shipping: msdb.dbo.log_shipping_primary_databases / log_shipping_secondary_databases
GO


/* ----------------------------------------------------------------------------
   Forma usada DENTRO da aplicação (segura contra injeção):
   o nome do banco entra por parâmetro e é protegido por QUOTENAME.
   ---------------------------------------------------------------------------- */
DECLARE @db sysname = N'NomeDoBanco';
DECLARE @cmd nvarchar(max);

SET @cmd = N'DBCC CHECKDB (' + QUOTENAME(@db) + N') WITH NO_INFOMSGS, ALL_ERRORMSGS;';
EXEC sys.sp_executesql @cmd;
GO
