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
   Forma usada DENTRO da aplicação (segura contra injeção):
   o nome do banco entra por parâmetro e é protegido por QUOTENAME.
   ---------------------------------------------------------------------------- */
DECLARE @db sysname = N'NomeDoBanco';
DECLARE @cmd nvarchar(max);

SET @cmd = N'DBCC CHECKDB (' + QUOTENAME(@db) + N') WITH NO_INFOMSGS, ALL_ERRORMSGS;';
EXEC sys.sp_executesql @cmd;
GO
