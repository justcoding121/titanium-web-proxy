{{/* Chart name, truncated for the 63 character Kubernetes limit. */}}
{{- define "titanium-manager.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" }}
{{- end }}

{{- define "titanium-manager.fullname" -}}
{{- if .Values.fullnameOverride }}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" }}
{{- else }}
{{- $name := default .Chart.Name .Values.nameOverride }}
{{- if contains $name .Release.Name }}
{{- .Release.Name | trunc 63 | trimSuffix "-" }}
{{- else }}
{{- printf "%s-%s" .Release.Name $name | trunc 63 | trimSuffix "-" }}
{{- end }}
{{- end }}
{{- end }}

{{- define "titanium-manager.chart" -}}
{{- printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" }}
{{- end }}

{{- define "titanium-manager.selectorLabels" -}}
app.kubernetes.io/name: {{ include "titanium-manager.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end }}

{{- define "titanium-manager.labels" -}}
helm.sh/chart: {{ include "titanium-manager.chart" . }}
{{ include "titanium-manager.selectorLabels" . }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end }}

{{- define "titanium-manager.serviceAccountName" -}}
{{- if .Values.serviceAccount.create }}
{{- default (include "titanium-manager.fullname" .) .Values.serviceAccount.name }}
{{- else }}
{{- default "default" .Values.serviceAccount.name }}
{{- end }}
{{- end }}

{{- define "titanium-manager.image" -}}
{{- if .Values.image.digest }}
{{- printf "%s@%s" .Values.image.repository .Values.image.digest }}
{{- else }}
{{- printf "%s:%s" .Values.image.repository (default .Chart.AppVersion .Values.image.tag) }}
{{- end }}
{{- end }}

{{/* The Secret that holds the admin bearer token: the user's, or the one the chart creates. */}}
{{- define "titanium-manager.adminSecretName" -}}
{{- default (printf "%s-admin" (include "titanium-manager.fullname" .)) .Values.admin.token.existingSecret }}
{{- end }}

{{- define "titanium-manager.pvcName" -}}
{{- default (include "titanium-manager.fullname" .) .Values.persistence.existingClaim }}
{{- end }}
