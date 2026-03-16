-- =============================================================================
-- missing-indexes.sql
-- Generate missing index recommendations in AdventureWorksLT
--
-- Run with: sqlcmd -S yourserver.database.windows.net -d AdventureWorksLT -G -i missing-indexes.sql
--   -G = Entra ID (Azure AD) authentication
--
-- These queries filter on non-indexed columns, forcing table/index scans.
-- After ~60s, database watcher will capture the missing index DMV data.
-- =============================================================================

SET NOCOUNT ON;
PRINT '=== Missing Index Workload Starting ===';
PRINT 'Time: ' + CONVERT(VARCHAR, GETUTCDATE(), 126) + 'Z';

-- ---------------------------------------------------------------------------
-- 1. Filter on SalesLT.Product.Color (no index)
-- ---------------------------------------------------------------------------
PRINT '';
PRINT '>> Querying Product by Color (no index)...';

DECLARE @i INT = 0;
WHILE @i < 50
BEGIN
    SELECT ProductID, Name, ProductNumber, ListPrice
    FROM SalesLT.Product
    WHERE Color = 'Red'
      AND ListPrice > 100.00
    ORDER BY ListPrice DESC;

    SELECT ProductID, Name, ProductNumber, ListPrice
    FROM SalesLT.Product
    WHERE Color = 'Black'
      AND Weight > 500;

    SET @i = @i + 1;
END;

PRINT '   Done: 100 queries on Product.Color';

-- ---------------------------------------------------------------------------
-- 2. Filter on SalesLT.SalesOrderDetail.UnitPrice (no index on this column)
-- ---------------------------------------------------------------------------
PRINT '';
PRINT '>> Querying SalesOrderDetail by UnitPrice (no index)...';

SET @i = 0;
WHILE @i < 50
BEGIN
    SELECT SalesOrderID, ProductID, OrderQty, UnitPrice, LineTotal
    FROM SalesLT.SalesOrderDetail
    WHERE UnitPrice > 1000.00
    ORDER BY UnitPrice DESC;

    SELECT SalesOrderID, ProductID, OrderQty, UnitPrice, LineTotal
    FROM SalesLT.SalesOrderDetail
    WHERE UnitPrice BETWEEN 50.00 AND 100.00
      AND OrderQty > 5;

    SET @i = @i + 1;
END;

PRINT '   Done: 100 queries on SalesOrderDetail.UnitPrice';

-- ---------------------------------------------------------------------------
-- 3. Filter on SalesLT.Customer.CompanyName (no index)
-- ---------------------------------------------------------------------------
PRINT '';
PRINT '>> Querying Customer by CompanyName (no index)...';

SET @i = 0;
WHILE @i < 50
BEGIN
    SELECT CustomerID, FirstName, LastName, CompanyName, EmailAddress
    FROM SalesLT.Customer
    WHERE CompanyName LIKE 'A%'
    ORDER BY CompanyName;

    SELECT CustomerID, FirstName, LastName, CompanyName
    FROM SalesLT.Customer
    WHERE CompanyName = 'Metropolitan Sports Supply';

    SET @i = @i + 1;
END;

PRINT '   Done: 100 queries on Customer.CompanyName';

-- ---------------------------------------------------------------------------
-- 4. Filter on SalesLT.SalesOrderHeader.OrderDate (no index)
-- ---------------------------------------------------------------------------
PRINT '';
PRINT '>> Querying SalesOrderHeader by OrderDate (no index)...';

SET @i = 0;
WHILE @i < 50
BEGIN
    SELECT SalesOrderID, OrderDate, DueDate, CustomerID, TotalDue
    FROM SalesLT.SalesOrderHeader
    WHERE OrderDate > '2008-06-01'
      AND TotalDue > 1000.00
    ORDER BY OrderDate DESC;

    SET @i = @i + 1;
END;

PRINT '   Done: 50 queries on SalesOrderHeader.OrderDate';

-- ---------------------------------------------------------------------------
-- 5. Join with filter on non-indexed columns
-- ---------------------------------------------------------------------------
PRINT '';
PRINT '>> Joining Product + SalesOrderDetail with non-indexed filters...';

SET @i = 0;
WHILE @i < 50
BEGIN
    SELECT p.Name, p.Color, d.OrderQty, d.UnitPrice, d.LineTotal
    FROM SalesLT.SalesOrderDetail d
    JOIN SalesLT.Product p ON d.ProductID = p.ProductID
    WHERE p.Color = 'Silver'
      AND d.UnitPrice > 500.00
    ORDER BY d.LineTotal DESC;

    SET @i = @i + 1;
END;

PRINT '   Done: 50 join queries on Color + UnitPrice';

PRINT '';
PRINT '=== Missing Index Workload Complete ===';
PRINT 'Time: ' + CONVERT(VARCHAR, GETUTCDATE(), 126) + 'Z';
PRINT '';
PRINT 'Wait ~60s for database watcher to collect telemetry, then use:';
PRINT '  history_indexes_missing to see the recommendations';
PRINT '  history_queries ordered by reads to see the scan-heavy queries';
GO
