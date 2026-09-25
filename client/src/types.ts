// Mirrors the backend Domain + Models types.

export type ConnectionKind = 'SapB1' | 'SqlServer' | 'File'

export interface ConnectionDef {
  id: string
  name: string
  kind: ConnectionKind | number
  baseUrl?: string
  companyDB?: string
  userName?: string
  ignoreSslErrors: boolean
  hasSecret: boolean
  lastTestedUtc?: string
  lastTestResult?: string
}

export interface SourceColumn { name: string; dataType: string; nullable: boolean }

export interface SourceSchema {
  sourceType: string
  objectName: string
  columns: SourceColumn[]
  previewRows: Record<string, unknown>[]
  totalRows?: number
}

export interface B1Property {
  name: string
  type: string
  nullable: boolean
  maxLength?: number
  isKey: boolean
  isUdf: boolean
  enumMembers?: string[]
}

export interface B1NavCollection { name: string; targetEntity: string; properties: B1Property[] }

export interface B1Entity {
  name: string
  entityType: string
  properties: B1Property[]
  collections: B1NavCollection[]
  hasUdfs: boolean
}

export interface EntitySummary {
  name: string
  hasUdfs: boolean
  fieldCount: number
  mandatoryCount: number
  collections: string[]
}

// ---- Mapping ----

export type TransformKind =
  | 'Direct' | 'Constant' | 'Expression' | 'DateFormat' | 'StaticLookup' | 'B1Lookup'

export interface B1LookupSpec {
  entity: string
  matchField: string
  returnField: string
  failIfMissing: boolean
  extraFilter?: string
}

export interface FieldSpec {
  targetField: string
  sourceColumn?: string | null
  transform: TransformKind
  constantValue?: string | null
  expression?: string | null
  dateFormat?: string | null
  staticLookup?: Record<string, string> | null
  b1Lookup?: B1LookupSpec | null
  defaultValue?: string | null
  required: boolean
  skipRowIfEmpty: boolean
}

export interface LineSpec {
  targetCollection: string
  fields: FieldSpec[]
  skipEmptyLines: boolean
}

export interface MappingSpec {
  header: FieldSpec[]
  lines: LineSpec[]
  groupBy?: string | null
  sourceKeyColumn?: string | null
}

export interface FieldNote { targetField: string; confidence: string; reason: string }

// ---- Scenario / Run ----

export type TriggerKind = 'Manual' | 'Schedule' | number
export type WriteMode = 'Create' | 'Upsert' | 'UpdateOnly' | number

export interface Scenario {
  id: string
  name: string
  description?: string
  enabled: boolean
  sourceConnectionId: string
  sourceKind: string
  sourceQuery?: string
  sourceObject?: string
  b1ConnectionId: string
  targetEntity: string
  writeMode: WriteMode
  keyFields?: string
  mappingJson: string
  trigger: TriggerKind
  cronExpression?: string
  continueOnError: boolean
  batchSize: number
  useBatch: boolean
  maxRetries: number
  watermarkColumn?: string
  watermarkValue?: string
  createdUtc?: string
  updatedUtc?: string
  nextRunUtc?: string
  lastRunUtc?: string
  lastRunStatus?: string
}

export type RunStatus =
  | 'Queued' | 'Running' | 'Succeeded' | 'PartiallyFailed' | 'Failed' | 'Cancelled' | number

export interface Run {
  id: string
  scenarioId: string
  scenarioName: string
  status: RunStatus
  dryRun: boolean
  triggeredBy: string
  queuedUtc: string
  startedUtc?: string
  finishedUtc?: string
  total: number
  processed: number
  succeeded: number
  failed: number
  skipped: number
  error?: string
  retryOfRunId?: string
}

export interface RunItem {
  id: string
  rowNumber: number
  status: 'Pending' | 'Ok' | 'Failed' | 'Skipped' | number
  sourceKey?: string
  targetKey?: string
  error?: string
  errorStage?: string
  payloadJson?: string
  attempts: number
}

export interface Dashboard {
  scenarios: number
  enabled: number
  scheduled: number
  activeRuns: number
  last7Days: { runs: number; records: number; failures: number }
  latest: Run[]
}
