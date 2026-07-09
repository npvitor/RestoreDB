/* ============================================================================
   RestoreDB — Cenários de TESTE (ambiente NÃO produtivo!)
   Compatível com SQL Server 2008 R2.

   Use estes scripts para gerar estados problemáticos e exercitar a ferramenta
   com segurança. NÃO execute em produção.
   ============================================================================ */

/* Banco de exemplo do módulo de faturamento ------------------------------- */
IF DB_ID(N'Faturamento_Teste') IS NULL
    CREATE DATABASE [Faturamento_Teste];
GO

USE [Faturamento_Teste];
GO
IF OBJECT_ID(N'dbo.NotaFiscal', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.NotaFiscal
    (
        NotaFiscalId int IDENTITY(1,1) PRIMARY KEY,
        Numero       int          NOT NULL,
        Cliente      nvarchar(120) NOT NULL,
        Valor        decimal(18,2) NOT NULL,
        Emissao      datetime      NOT NULL DEFAULT (GETDATE())
    );

    INSERT INTO dbo.NotaFiscal (Numero, Cliente, Valor)
    VALUES (1001, N'Cliente A', 250.00),
           (1002, N'Cliente B', 980.50),
           (1003, N'Cliente C', 1200.00);
END
GO
-- Tabela FILHA com FK para NotaFiscal — exercita a recuperação automática de registros-pai
-- órfãos a partir de backup (antes de excluir os itens saudáveis cujo pai sumiu no reparo).
IF OBJECT_ID(N'dbo.ItemNotaFiscal', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ItemNotaFiscal
    (
        ItemNotaFiscalId int IDENTITY(1,1) PRIMARY KEY,
        NotaFiscalId     int           NOT NULL,
        Descricao        nvarchar(200) NOT NULL,
        Quantidade       int           NOT NULL DEFAULT (1),
        CONSTRAINT FK_ItemNotaFiscal_NotaFiscal
            FOREIGN KEY (NotaFiscalId) REFERENCES dbo.NotaFiscal (NotaFiscalId)
    );

    INSERT INTO dbo.ItemNotaFiscal (NotaFiscalId, Descricao, Quantidade)
    VALUES (1, N'Item da NF 1001', 2),
           (2, N'Item da NF 1002', 1),
           (3, N'Item da NF 1003', 5);
END
GO
USE [master];
GO


/* ----------------------------------------------------------------------------
   CENÁRIO 1 — OFFLINE (reparo SEM perda de dados: SET ONLINE)
   ---------------------------------------------------------------------------- */
-- Provocar:
ALTER DATABASE [Faturamento_Teste] SET OFFLINE WITH ROLLBACK IMMEDIATE;
-- Agora abra a ferramenta: o banco aparecerá como OFFLINE.
-- Reparo esperado: ALTER DATABASE ... SET ONLINE.
GO


/* ----------------------------------------------------------------------------
   CENÁRIO 2 — RESTORING (reparo SEM perda de dados: RESTORE WITH RECOVERY)
   ---------------------------------------------------------------------------- */
-- Provocar (faz um backup e restaura deixando em modo de recuperação):
BACKUP DATABASE [Faturamento_Teste]
    TO DISK = N'Faturamento_Teste_teste.bak' WITH INIT, COPY_ONLY;
RESTORE DATABASE [Faturamento_Teste]
    FROM DISK = N'Faturamento_Teste_teste.bak'
    WITH NORECOVERY, REPLACE;
-- Agora o banco aparece como RESTORING.
-- Reparo esperado: RESTORE DATABASE ... WITH RECOVERY.
GO


/* ----------------------------------------------------------------------------
   CENÁRIO 3 — EMERGENCY (exercita o fluxo DBCC CHECKDB)
   ---------------------------------------------------------------------------- */
-- Provocar (estado de emergência, sem corromper arquivos):
ALTER DATABASE [Faturamento_Teste] SET EMERGENCY;
-- A ferramenta executará: SINGLE_USER -> DBCC CHECKDB -> REPAIR_REBUILD -> verificação -> MULTI_USER.
-- Como não há corrupção real, o reparo seguro tende a concluir e o banco volta a ONLINE.
GO

/* Observação: SUSPECT / corrupção real só ocorre com dano físico aos arquivos
   (.mdf/.ldf), o que NÃO deve ser simulado em ambientes compartilhados. O fluxo
   de REPAIR_ALLOW_DATA_LOSS é acionado pela ferramenta apenas quando o reparo
   seguro não resolve, e sempre com confirmação explícita do usuário. */


/* ----------------------------------------------------------------------------
   CENÁRIO 4 — RESTAURAR DE BACKUP (testa o novo fluxo de restauração)
   ---------------------------------------------------------------------------- */
-- Gere um backup completo para testar a restauração na ferramenta:
BACKUP DATABASE [Faturamento_Teste]
    TO DISK = N'Faturamento_Teste_full.bak' WITH INIT, COPY_ONLY, NAME = N'Teste full';
-- Na ferramenta: diagnostique o banco, abra "RESTAURAR DE BACKUP", escolha o .bak
-- (ou selecione no histórico) e restaure com REPLACE.
GO


/* ----------------------------------------------------------------------------
   CENÁRIO 5 — Pré-requisitos (testa as pré-checagens)
   ---------------------------------------------------------------------------- */
-- Conecte-se com um login NÃO sysadmin: a ferramenta deve avisar antes de prosseguir.
-- Conecte-se com autenticação do Windows: marque a caixa na tela de conexão.
--
-- Reparo SEM perda (índices): a ferramenta detecta automaticamente, via
-- DBCC CHECKDB ... WITH TABLERESULTS, quando a corrupção está apenas em índices
-- NÃO clusterizados (index_id > 1) e oferece "Reparar índices (sem perda de dados)".
-- Isso exige corrupção física real e, portanto, não é simulável com segurança aqui.
GO
