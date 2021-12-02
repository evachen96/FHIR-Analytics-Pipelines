CREATE EXTERNAL TABLE [fhir].[MedicinalProductIndication] (
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
    [subject] VARCHAR(MAX),
    [diseaseSymptomProcedure.id] NVARCHAR(4000),
    [diseaseSymptomProcedure.extension] NVARCHAR(MAX),
    [diseaseSymptomProcedure.coding] VARCHAR(MAX),
    [diseaseSymptomProcedure.text] NVARCHAR(4000),
    [diseaseStatus.id] NVARCHAR(4000),
    [diseaseStatus.extension] NVARCHAR(MAX),
    [diseaseStatus.coding] VARCHAR(MAX),
    [diseaseStatus.text] NVARCHAR(4000),
    [comorbidity] VARCHAR(MAX),
    [intendedEffect.id] NVARCHAR(4000),
    [intendedEffect.extension] NVARCHAR(MAX),
    [intendedEffect.coding] VARCHAR(MAX),
    [intendedEffect.text] NVARCHAR(4000),
    [duration.id] NVARCHAR(4000),
    [duration.extension] NVARCHAR(MAX),
    [duration.value] float,
    [duration.comparator] NVARCHAR(64),
    [duration.unit] NVARCHAR(4000),
    [duration.system] VARCHAR(256),
    [duration.code] NVARCHAR(4000),
    [otherTherapy] VARCHAR(MAX),
    [undesirableEffect] VARCHAR(MAX),
    [population] VARCHAR(MAX),
) WITH (
    LOCATION='/MedicinalProductIndication/**',
    DATA_SOURCE = ParquetSource,
    FILE_FORMAT = ParquetFormat
);

GO

CREATE VIEW fhir.MedicinalProductIndicationSubject AS
SELECT
    [id],
    [subject.JSON],
    [subject.id],
    [subject.extension],
    [subject.reference],
    [subject.type],
    [subject.identifier.id],
    [subject.identifier.extension],
    [subject.identifier.use],
    [subject.identifier.type],
    [subject.identifier.system],
    [subject.identifier.value],
    [subject.identifier.period],
    [subject.identifier.assigner],
    [subject.display]
FROM openrowset (
        BULK 'MedicinalProductIndication/**',
        DATA_SOURCE = 'ParquetSource',
        FORMAT = 'PARQUET'
    ) WITH (
        [id]   VARCHAR(64),
       [subject.JSON]  VARCHAR(MAX) '$.subject'
    ) AS rowset
    CROSS APPLY openjson (rowset.[subject.JSON]) with (
        [subject.id]                   NVARCHAR(4000)      '$.id',
        [subject.extension]            NVARCHAR(MAX)       '$.extension',
        [subject.reference]            NVARCHAR(4000)      '$.reference',
        [subject.type]                 VARCHAR(256)        '$.type',
        [subject.identifier.id]        NVARCHAR(4000)      '$.identifier.id',
        [subject.identifier.extension] NVARCHAR(MAX)       '$.identifier.extension',
        [subject.identifier.use]       NVARCHAR(64)        '$.identifier.use',
        [subject.identifier.type]      NVARCHAR(MAX)       '$.identifier.type',
        [subject.identifier.system]    VARCHAR(256)        '$.identifier.system',
        [subject.identifier.value]     NVARCHAR(4000)      '$.identifier.value',
        [subject.identifier.period]    NVARCHAR(MAX)       '$.identifier.period',
        [subject.identifier.assigner]  NVARCHAR(MAX)       '$.identifier.assigner',
        [subject.display]              NVARCHAR(4000)      '$.display'
    ) j

GO

CREATE VIEW fhir.MedicinalProductIndicationComorbidity AS
SELECT
    [id],
    [comorbidity.JSON],
    [comorbidity.id],
    [comorbidity.extension],
    [comorbidity.coding],
    [comorbidity.text]
FROM openrowset (
        BULK 'MedicinalProductIndication/**',
        DATA_SOURCE = 'ParquetSource',
        FORMAT = 'PARQUET'
    ) WITH (
        [id]   VARCHAR(64),
       [comorbidity.JSON]  VARCHAR(MAX) '$.comorbidity'
    ) AS rowset
    CROSS APPLY openjson (rowset.[comorbidity.JSON]) with (
        [comorbidity.id]               NVARCHAR(4000)      '$.id',
        [comorbidity.extension]        NVARCHAR(MAX)       '$.extension',
        [comorbidity.coding]           NVARCHAR(MAX)       '$.coding' AS JSON,
        [comorbidity.text]             NVARCHAR(4000)      '$.text'
    ) j

GO

CREATE VIEW fhir.MedicinalProductIndicationOtherTherapy AS
SELECT
    [id],
    [otherTherapy.JSON],
    [otherTherapy.id],
    [otherTherapy.extension],
    [otherTherapy.modifierExtension],
    [otherTherapy.therapyRelationshipType.id],
    [otherTherapy.therapyRelationshipType.extension],
    [otherTherapy.therapyRelationshipType.coding],
    [otherTherapy.therapyRelationshipType.text],
    [otherTherapy.medication.CodeableConcept.id],
    [otherTherapy.medication.CodeableConcept.extension],
    [otherTherapy.medication.CodeableConcept.coding],
    [otherTherapy.medication.CodeableConcept.text],
    [otherTherapy.medication.Reference.id],
    [otherTherapy.medication.Reference.extension],
    [otherTherapy.medication.Reference.reference],
    [otherTherapy.medication.Reference.type],
    [otherTherapy.medication.Reference.identifier],
    [otherTherapy.medication.Reference.display]
FROM openrowset (
        BULK 'MedicinalProductIndication/**',
        DATA_SOURCE = 'ParquetSource',
        FORMAT = 'PARQUET'
    ) WITH (
        [id]   VARCHAR(64),
       [otherTherapy.JSON]  VARCHAR(MAX) '$.otherTherapy'
    ) AS rowset
    CROSS APPLY openjson (rowset.[otherTherapy.JSON]) with (
        [otherTherapy.id]              NVARCHAR(4000)      '$.id',
        [otherTherapy.extension]       NVARCHAR(MAX)       '$.extension',
        [otherTherapy.modifierExtension] NVARCHAR(MAX)       '$.modifierExtension',
        [otherTherapy.therapyRelationshipType.id] NVARCHAR(4000)      '$.therapyRelationshipType.id',
        [otherTherapy.therapyRelationshipType.extension] NVARCHAR(MAX)       '$.therapyRelationshipType.extension',
        [otherTherapy.therapyRelationshipType.coding] NVARCHAR(MAX)       '$.therapyRelationshipType.coding',
        [otherTherapy.therapyRelationshipType.text] NVARCHAR(4000)      '$.therapyRelationshipType.text',
        [otherTherapy.medication.CodeableConcept.id] NVARCHAR(4000)      '$.medication.CodeableConcept.id',
        [otherTherapy.medication.CodeableConcept.extension] NVARCHAR(MAX)       '$.medication.CodeableConcept.extension',
        [otherTherapy.medication.CodeableConcept.coding] NVARCHAR(MAX)       '$.medication.CodeableConcept.coding',
        [otherTherapy.medication.CodeableConcept.text] NVARCHAR(4000)      '$.medication.CodeableConcept.text',
        [otherTherapy.medication.Reference.id] NVARCHAR(4000)      '$.medication.Reference.id',
        [otherTherapy.medication.Reference.extension] NVARCHAR(MAX)       '$.medication.Reference.extension',
        [otherTherapy.medication.Reference.reference] NVARCHAR(4000)      '$.medication.Reference.reference',
        [otherTherapy.medication.Reference.type] VARCHAR(256)        '$.medication.Reference.type',
        [otherTherapy.medication.Reference.identifier] NVARCHAR(MAX)       '$.medication.Reference.identifier',
        [otherTherapy.medication.Reference.display] NVARCHAR(4000)      '$.medication.Reference.display'
    ) j

GO

CREATE VIEW fhir.MedicinalProductIndicationUndesirableEffect AS
SELECT
    [id],
    [undesirableEffect.JSON],
    [undesirableEffect.id],
    [undesirableEffect.extension],
    [undesirableEffect.reference],
    [undesirableEffect.type],
    [undesirableEffect.identifier.id],
    [undesirableEffect.identifier.extension],
    [undesirableEffect.identifier.use],
    [undesirableEffect.identifier.type],
    [undesirableEffect.identifier.system],
    [undesirableEffect.identifier.value],
    [undesirableEffect.identifier.period],
    [undesirableEffect.identifier.assigner],
    [undesirableEffect.display]
FROM openrowset (
        BULK 'MedicinalProductIndication/**',
        DATA_SOURCE = 'ParquetSource',
        FORMAT = 'PARQUET'
    ) WITH (
        [id]   VARCHAR(64),
       [undesirableEffect.JSON]  VARCHAR(MAX) '$.undesirableEffect'
    ) AS rowset
    CROSS APPLY openjson (rowset.[undesirableEffect.JSON]) with (
        [undesirableEffect.id]         NVARCHAR(4000)      '$.id',
        [undesirableEffect.extension]  NVARCHAR(MAX)       '$.extension',
        [undesirableEffect.reference]  NVARCHAR(4000)      '$.reference',
        [undesirableEffect.type]       VARCHAR(256)        '$.type',
        [undesirableEffect.identifier.id] NVARCHAR(4000)      '$.identifier.id',
        [undesirableEffect.identifier.extension] NVARCHAR(MAX)       '$.identifier.extension',
        [undesirableEffect.identifier.use] NVARCHAR(64)        '$.identifier.use',
        [undesirableEffect.identifier.type] NVARCHAR(MAX)       '$.identifier.type',
        [undesirableEffect.identifier.system] VARCHAR(256)        '$.identifier.system',
        [undesirableEffect.identifier.value] NVARCHAR(4000)      '$.identifier.value',
        [undesirableEffect.identifier.period] NVARCHAR(MAX)       '$.identifier.period',
        [undesirableEffect.identifier.assigner] NVARCHAR(MAX)       '$.identifier.assigner',
        [undesirableEffect.display]    NVARCHAR(4000)      '$.display'
    ) j

GO

CREATE VIEW fhir.MedicinalProductIndicationPopulation AS
SELECT
    [id],
    [population.JSON],
    [population.id],
    [population.extension],
    [population.modifierExtension],
    [population.gender.id],
    [population.gender.extension],
    [population.gender.coding],
    [population.gender.text],
    [population.race.id],
    [population.race.extension],
    [population.race.coding],
    [population.race.text],
    [population.physiologicalCondition.id],
    [population.physiologicalCondition.extension],
    [population.physiologicalCondition.coding],
    [population.physiologicalCondition.text],
    [population.age.Range.id],
    [population.age.Range.extension],
    [population.age.Range.low],
    [population.age.Range.high],
    [population.age.CodeableConcept.id],
    [population.age.CodeableConcept.extension],
    [population.age.CodeableConcept.coding],
    [population.age.CodeableConcept.text]
FROM openrowset (
        BULK 'MedicinalProductIndication/**',
        DATA_SOURCE = 'ParquetSource',
        FORMAT = 'PARQUET'
    ) WITH (
        [id]   VARCHAR(64),
       [population.JSON]  VARCHAR(MAX) '$.population'
    ) AS rowset
    CROSS APPLY openjson (rowset.[population.JSON]) with (
        [population.id]                NVARCHAR(4000)      '$.id',
        [population.extension]         NVARCHAR(MAX)       '$.extension',
        [population.modifierExtension] NVARCHAR(MAX)       '$.modifierExtension',
        [population.gender.id]         NVARCHAR(4000)      '$.gender.id',
        [population.gender.extension]  NVARCHAR(MAX)       '$.gender.extension',
        [population.gender.coding]     NVARCHAR(MAX)       '$.gender.coding',
        [population.gender.text]       NVARCHAR(4000)      '$.gender.text',
        [population.race.id]           NVARCHAR(4000)      '$.race.id',
        [population.race.extension]    NVARCHAR(MAX)       '$.race.extension',
        [population.race.coding]       NVARCHAR(MAX)       '$.race.coding',
        [population.race.text]         NVARCHAR(4000)      '$.race.text',
        [population.physiologicalCondition.id] NVARCHAR(4000)      '$.physiologicalCondition.id',
        [population.physiologicalCondition.extension] NVARCHAR(MAX)       '$.physiologicalCondition.extension',
        [population.physiologicalCondition.coding] NVARCHAR(MAX)       '$.physiologicalCondition.coding',
        [population.physiologicalCondition.text] NVARCHAR(4000)      '$.physiologicalCondition.text',
        [population.age.Range.id]      NVARCHAR(4000)      '$.age.Range.id',
        [population.age.Range.extension] NVARCHAR(MAX)       '$.age.Range.extension',
        [population.age.Range.low]     NVARCHAR(MAX)       '$.age.Range.low',
        [population.age.Range.high]    NVARCHAR(MAX)       '$.age.Range.high',
        [population.age.CodeableConcept.id] NVARCHAR(4000)      '$.age.CodeableConcept.id',
        [population.age.CodeableConcept.extension] NVARCHAR(MAX)       '$.age.CodeableConcept.extension',
        [population.age.CodeableConcept.coding] NVARCHAR(MAX)       '$.age.CodeableConcept.coding',
        [population.age.CodeableConcept.text] NVARCHAR(4000)      '$.age.CodeableConcept.text'
    ) j