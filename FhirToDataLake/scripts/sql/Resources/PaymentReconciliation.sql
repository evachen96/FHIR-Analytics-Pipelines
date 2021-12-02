CREATE EXTERNAL TABLE [fhir].[PaymentReconciliation] (
    [resourceType] NVARCHAR(4000),
    [id] VARCHAR(64),
    [meta.id] NVARCHAR(4000),
    [meta.extension] NVARCHAR(MAX),
    [meta.versionId] VARCHAR(64),
    [meta.lastUpdated] VARCHAR(30),
    [meta.source] VARCHAR(256),
    [meta.profile] VARCHAR(MAX),
    [meta.security] VARCHAR(MAX),
    [meta.tag] VARCHAR(MAX),
    [implicitRules] VARCHAR(256),
    [language] NVARCHAR(4000),
    [text.id] NVARCHAR(4000),
    [text.extension] NVARCHAR(MAX),
    [text.status] NVARCHAR(64),
    [text.div] NVARCHAR(MAX),
    [extension] NVARCHAR(MAX),
    [modifierExtension] NVARCHAR(MAX),
    [identifier] VARCHAR(MAX),
    [status] NVARCHAR(4000),
    [period.id] NVARCHAR(4000),
    [period.extension] NVARCHAR(MAX),
    [period.start] VARCHAR(30),
    [period.end] VARCHAR(30),
    [created] VARCHAR(30),
    [paymentIssuer.id] NVARCHAR(4000),
    [paymentIssuer.extension] NVARCHAR(MAX),
    [paymentIssuer.reference] NVARCHAR(4000),
    [paymentIssuer.type] VARCHAR(256),
    [paymentIssuer.identifier.id] NVARCHAR(4000),
    [paymentIssuer.identifier.extension] NVARCHAR(MAX),
    [paymentIssuer.identifier.use] NVARCHAR(64),
    [paymentIssuer.identifier.type] NVARCHAR(MAX),
    [paymentIssuer.identifier.system] VARCHAR(256),
    [paymentIssuer.identifier.value] NVARCHAR(4000),
    [paymentIssuer.identifier.period] NVARCHAR(MAX),
    [paymentIssuer.identifier.assigner] NVARCHAR(MAX),
    [paymentIssuer.display] NVARCHAR(4000),
    [request.id] NVARCHAR(4000),
    [request.extension] NVARCHAR(MAX),
    [request.reference] NVARCHAR(4000),
    [request.type] VARCHAR(256),
    [request.identifier.id] NVARCHAR(4000),
    [request.identifier.extension] NVARCHAR(MAX),
    [request.identifier.use] NVARCHAR(64),
    [request.identifier.type] NVARCHAR(MAX),
    [request.identifier.system] VARCHAR(256),
    [request.identifier.value] NVARCHAR(4000),
    [request.identifier.period] NVARCHAR(MAX),
    [request.identifier.assigner] NVARCHAR(MAX),
    [request.display] NVARCHAR(4000),
    [requestor.id] NVARCHAR(4000),
    [requestor.extension] NVARCHAR(MAX),
    [requestor.reference] NVARCHAR(4000),
    [requestor.type] VARCHAR(256),
    [requestor.identifier.id] NVARCHAR(4000),
    [requestor.identifier.extension] NVARCHAR(MAX),
    [requestor.identifier.use] NVARCHAR(64),
    [requestor.identifier.type] NVARCHAR(MAX),
    [requestor.identifier.system] VARCHAR(256),
    [requestor.identifier.value] NVARCHAR(4000),
    [requestor.identifier.period] NVARCHAR(MAX),
    [requestor.identifier.assigner] NVARCHAR(MAX),
    [requestor.display] NVARCHAR(4000),
    [outcome] NVARCHAR(64),
    [disposition] NVARCHAR(4000),
    [paymentDate] VARCHAR(10),
    [paymentAmount.id] NVARCHAR(4000),
    [paymentAmount.extension] NVARCHAR(MAX),
    [paymentAmount.value] float,
    [paymentAmount.currency] NVARCHAR(4000),
    [paymentIdentifier.id] NVARCHAR(4000),
    [paymentIdentifier.extension] NVARCHAR(MAX),
    [paymentIdentifier.use] NVARCHAR(64),
    [paymentIdentifier.type.id] NVARCHAR(4000),
    [paymentIdentifier.type.extension] NVARCHAR(MAX),
    [paymentIdentifier.type.coding] NVARCHAR(MAX),
    [paymentIdentifier.type.text] NVARCHAR(4000),
    [paymentIdentifier.system] VARCHAR(256),
    [paymentIdentifier.value] NVARCHAR(4000),
    [paymentIdentifier.period.id] NVARCHAR(4000),
    [paymentIdentifier.period.extension] NVARCHAR(MAX),
    [paymentIdentifier.period.start] VARCHAR(30),
    [paymentIdentifier.period.end] VARCHAR(30),
    [paymentIdentifier.assigner.id] NVARCHAR(4000),
    [paymentIdentifier.assigner.extension] NVARCHAR(MAX),
    [paymentIdentifier.assigner.reference] NVARCHAR(4000),
    [paymentIdentifier.assigner.type] VARCHAR(256),
    [paymentIdentifier.assigner.identifier] NVARCHAR(MAX),
    [paymentIdentifier.assigner.display] NVARCHAR(4000),
    [detail] VARCHAR(MAX),
    [formCode.id] NVARCHAR(4000),
    [formCode.extension] NVARCHAR(MAX),
    [formCode.coding] VARCHAR(MAX),
    [formCode.text] NVARCHAR(4000),
    [processNote] VARCHAR(MAX),
) WITH (
    LOCATION='/PaymentReconciliation/**',
    DATA_SOURCE = ParquetSource,
    FILE_FORMAT = ParquetFormat
);

GO

CREATE VIEW fhir.PaymentReconciliationIdentifier AS
SELECT
    [id],
    [identifier.JSON],
    [identifier.id],
    [identifier.extension],
    [identifier.use],
    [identifier.type.id],
    [identifier.type.extension],
    [identifier.type.coding],
    [identifier.type.text],
    [identifier.system],
    [identifier.value],
    [identifier.period.id],
    [identifier.period.extension],
    [identifier.period.start],
    [identifier.period.end],
    [identifier.assigner.id],
    [identifier.assigner.extension],
    [identifier.assigner.reference],
    [identifier.assigner.type],
    [identifier.assigner.identifier],
    [identifier.assigner.display]
FROM openrowset (
        BULK 'PaymentReconciliation/**',
        DATA_SOURCE = 'ParquetSource',
        FORMAT = 'PARQUET'
    ) WITH (
        [id]   VARCHAR(64),
       [identifier.JSON]  VARCHAR(MAX) '$.identifier'
    ) AS rowset
    CROSS APPLY openjson (rowset.[identifier.JSON]) with (
        [identifier.id]                NVARCHAR(4000)      '$.id',
        [identifier.extension]         NVARCHAR(MAX)       '$.extension',
        [identifier.use]               NVARCHAR(64)        '$.use',
        [identifier.type.id]           NVARCHAR(4000)      '$.type.id',
        [identifier.type.extension]    NVARCHAR(MAX)       '$.type.extension',
        [identifier.type.coding]       NVARCHAR(MAX)       '$.type.coding',
        [identifier.type.text]         NVARCHAR(4000)      '$.type.text',
        [identifier.system]            VARCHAR(256)        '$.system',
        [identifier.value]             NVARCHAR(4000)      '$.value',
        [identifier.period.id]         NVARCHAR(4000)      '$.period.id',
        [identifier.period.extension]  NVARCHAR(MAX)       '$.period.extension',
        [identifier.period.start]      VARCHAR(30)         '$.period.start',
        [identifier.period.end]        VARCHAR(30)         '$.period.end',
        [identifier.assigner.id]       NVARCHAR(4000)      '$.assigner.id',
        [identifier.assigner.extension] NVARCHAR(MAX)       '$.assigner.extension',
        [identifier.assigner.reference] NVARCHAR(4000)      '$.assigner.reference',
        [identifier.assigner.type]     VARCHAR(256)        '$.assigner.type',
        [identifier.assigner.identifier] NVARCHAR(MAX)       '$.assigner.identifier',
        [identifier.assigner.display]  NVARCHAR(4000)      '$.assigner.display'
    ) j

GO

CREATE VIEW fhir.PaymentReconciliationDetail AS
SELECT
    [id],
    [detail.JSON],
    [detail.id],
    [detail.extension],
    [detail.modifierExtension],
    [detail.identifier.id],
    [detail.identifier.extension],
    [detail.identifier.use],
    [detail.identifier.type],
    [detail.identifier.system],
    [detail.identifier.value],
    [detail.identifier.period],
    [detail.identifier.assigner],
    [detail.predecessor.id],
    [detail.predecessor.extension],
    [detail.predecessor.use],
    [detail.predecessor.type],
    [detail.predecessor.system],
    [detail.predecessor.value],
    [detail.predecessor.period],
    [detail.predecessor.assigner],
    [detail.type.id],
    [detail.type.extension],
    [detail.type.coding],
    [detail.type.text],
    [detail.request.id],
    [detail.request.extension],
    [detail.request.reference],
    [detail.request.type],
    [detail.request.identifier],
    [detail.request.display],
    [detail.submitter.id],
    [detail.submitter.extension],
    [detail.submitter.reference],
    [detail.submitter.type],
    [detail.submitter.identifier],
    [detail.submitter.display],
    [detail.response.id],
    [detail.response.extension],
    [detail.response.reference],
    [detail.response.type],
    [detail.response.identifier],
    [detail.response.display],
    [detail.date],
    [detail.responsible.id],
    [detail.responsible.extension],
    [detail.responsible.reference],
    [detail.responsible.type],
    [detail.responsible.identifier],
    [detail.responsible.display],
    [detail.payee.id],
    [detail.payee.extension],
    [detail.payee.reference],
    [detail.payee.type],
    [detail.payee.identifier],
    [detail.payee.display],
    [detail.amount.id],
    [detail.amount.extension],
    [detail.amount.value],
    [detail.amount.currency]
FROM openrowset (
        BULK 'PaymentReconciliation/**',
        DATA_SOURCE = 'ParquetSource',
        FORMAT = 'PARQUET'
    ) WITH (
        [id]   VARCHAR(64),
       [detail.JSON]  VARCHAR(MAX) '$.detail'
    ) AS rowset
    CROSS APPLY openjson (rowset.[detail.JSON]) with (
        [detail.id]                    NVARCHAR(4000)      '$.id',
        [detail.extension]             NVARCHAR(MAX)       '$.extension',
        [detail.modifierExtension]     NVARCHAR(MAX)       '$.modifierExtension',
        [detail.identifier.id]         NVARCHAR(4000)      '$.identifier.id',
        [detail.identifier.extension]  NVARCHAR(MAX)       '$.identifier.extension',
        [detail.identifier.use]        NVARCHAR(64)        '$.identifier.use',
        [detail.identifier.type]       NVARCHAR(MAX)       '$.identifier.type',
        [detail.identifier.system]     VARCHAR(256)        '$.identifier.system',
        [detail.identifier.value]      NVARCHAR(4000)      '$.identifier.value',
        [detail.identifier.period]     NVARCHAR(MAX)       '$.identifier.period',
        [detail.identifier.assigner]   NVARCHAR(MAX)       '$.identifier.assigner',
        [detail.predecessor.id]        NVARCHAR(4000)      '$.predecessor.id',
        [detail.predecessor.extension] NVARCHAR(MAX)       '$.predecessor.extension',
        [detail.predecessor.use]       NVARCHAR(64)        '$.predecessor.use',
        [detail.predecessor.type]      NVARCHAR(MAX)       '$.predecessor.type',
        [detail.predecessor.system]    VARCHAR(256)        '$.predecessor.system',
        [detail.predecessor.value]     NVARCHAR(4000)      '$.predecessor.value',
        [detail.predecessor.period]    NVARCHAR(MAX)       '$.predecessor.period',
        [detail.predecessor.assigner]  NVARCHAR(MAX)       '$.predecessor.assigner',
        [detail.type.id]               NVARCHAR(4000)      '$.type.id',
        [detail.type.extension]        NVARCHAR(MAX)       '$.type.extension',
        [detail.type.coding]           NVARCHAR(MAX)       '$.type.coding',
        [detail.type.text]             NVARCHAR(4000)      '$.type.text',
        [detail.request.id]            NVARCHAR(4000)      '$.request.id',
        [detail.request.extension]     NVARCHAR(MAX)       '$.request.extension',
        [detail.request.reference]     NVARCHAR(4000)      '$.request.reference',
        [detail.request.type]          VARCHAR(256)        '$.request.type',
        [detail.request.identifier]    NVARCHAR(MAX)       '$.request.identifier',
        [detail.request.display]       NVARCHAR(4000)      '$.request.display',
        [detail.submitter.id]          NVARCHAR(4000)      '$.submitter.id',
        [detail.submitter.extension]   NVARCHAR(MAX)       '$.submitter.extension',
        [detail.submitter.reference]   NVARCHAR(4000)      '$.submitter.reference',
        [detail.submitter.type]        VARCHAR(256)        '$.submitter.type',
        [detail.submitter.identifier]  NVARCHAR(MAX)       '$.submitter.identifier',
        [detail.submitter.display]     NVARCHAR(4000)      '$.submitter.display',
        [detail.response.id]           NVARCHAR(4000)      '$.response.id',
        [detail.response.extension]    NVARCHAR(MAX)       '$.response.extension',
        [detail.response.reference]    NVARCHAR(4000)      '$.response.reference',
        [detail.response.type]         VARCHAR(256)        '$.response.type',
        [detail.response.identifier]   NVARCHAR(MAX)       '$.response.identifier',
        [detail.response.display]      NVARCHAR(4000)      '$.response.display',
        [detail.date]                  VARCHAR(10)         '$.date',
        [detail.responsible.id]        NVARCHAR(4000)      '$.responsible.id',
        [detail.responsible.extension] NVARCHAR(MAX)       '$.responsible.extension',
        [detail.responsible.reference] NVARCHAR(4000)      '$.responsible.reference',
        [detail.responsible.type]      VARCHAR(256)        '$.responsible.type',
        [detail.responsible.identifier] NVARCHAR(MAX)       '$.responsible.identifier',
        [detail.responsible.display]   NVARCHAR(4000)      '$.responsible.display',
        [detail.payee.id]              NVARCHAR(4000)      '$.payee.id',
        [detail.payee.extension]       NVARCHAR(MAX)       '$.payee.extension',
        [detail.payee.reference]       NVARCHAR(4000)      '$.payee.reference',
        [detail.payee.type]            VARCHAR(256)        '$.payee.type',
        [detail.payee.identifier]      NVARCHAR(MAX)       '$.payee.identifier',
        [detail.payee.display]         NVARCHAR(4000)      '$.payee.display',
        [detail.amount.id]             NVARCHAR(4000)      '$.amount.id',
        [detail.amount.extension]      NVARCHAR(MAX)       '$.amount.extension',
        [detail.amount.value]          float               '$.amount.value',
        [detail.amount.currency]       NVARCHAR(4000)      '$.amount.currency'
    ) j

GO

CREATE VIEW fhir.PaymentReconciliationProcessNote AS
SELECT
    [id],
    [processNote.JSON],
    [processNote.id],
    [processNote.extension],
    [processNote.modifierExtension],
    [processNote.type],
    [processNote.text]
FROM openrowset (
        BULK 'PaymentReconciliation/**',
        DATA_SOURCE = 'ParquetSource',
        FORMAT = 'PARQUET'
    ) WITH (
        [id]   VARCHAR(64),
       [processNote.JSON]  VARCHAR(MAX) '$.processNote'
    ) AS rowset
    CROSS APPLY openjson (rowset.[processNote.JSON]) with (
        [processNote.id]               NVARCHAR(4000)      '$.id',
        [processNote.extension]        NVARCHAR(MAX)       '$.extension',
        [processNote.modifierExtension] NVARCHAR(MAX)       '$.modifierExtension',
        [processNote.type]             NVARCHAR(64)        '$.type',
        [processNote.text]             NVARCHAR(4000)      '$.text'
    ) j