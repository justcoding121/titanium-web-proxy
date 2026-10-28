{{/* Chart name, truncated for the 63 character Kubernetes limit. */}}
{{- define "titanium.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" }}
{{- end }}

{{- define "titanium.fullname" -}}
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

{{- define "titanium.chart" -}}
{{- printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" }}
{{- end }}

{{- define "titanium.selectorLabels" -}}
app.kubernetes.io/name: {{ include "titanium.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end }}

{{- define "titanium.labels" -}}
helm.sh/chart: {{ include "titanium.chart" . }}
{{ include "titanium.selectorLabels" . }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end }}

{{- define "titanium.serviceAccountName" -}}
{{- if .Values.serviceAccount.create }}
{{- default (include "titanium.fullname" .) .Values.serviceAccount.name }}
{{- else }}
{{- default "default" .Values.serviceAccount.name }}
{{- end }}
{{- end }}

{{- define "titanium.image" -}}
{{- if .Values.image.digest }}
{{- printf "%s@%s" .Values.image.repository .Values.image.digest }}
{{- else }}
{{- printf "%s:%s" .Values.image.repository (default .Chart.AppVersion .Values.image.tag) }}
{{- end }}
{{- end }}

{{/* The Secret that holds the admin bearer token: the user's, or the one the chart creates. */}}
{{- define "titanium.adminSecretName" -}}
{{- default (printf "%s-admin" (include "titanium.fullname" .)) .Values.admin.token.existingSecret }}
{{- end }}

{{- define "titanium.adminServiceEnabled" -}}
{{- if or .Values.admin.service.enabled .Values.serviceMonitor.enabled }}true{{ end }}
{{- end }}

{{/*
The twp.yaml the pods read. `config` is passed through; the chart owns server.admin so that the probes, the token
reference, and the pre-stop delay always agree with the Deployment.
*/}}
{{- define "titanium.config" -}}
{{- $cfg := deepCopy .Values.config -}}
{{- $server := default (dict) (get $cfg "server") -}}
{{- $admin := dict "enabled" true "host" "0.0.0.0" "port" (int .Values.admin.port) "authTokenEnv" "TITANIUM_ADMIN_TOKEN" "preStopDelaySeconds" (int .Values.admin.preStopDelaySeconds) "readiness" (dict "requireHealthyClusters" (.Values.admin.requireHealthyClusters | default false)) -}}
{{- $_ := set $server "admin" $admin -}}
{{- $_ := set $cfg "server" $server -}}
{{- if .Values.plus.enabled -}}
{{- $_ := required "plus.controlPlane.sharedSecret.existingSecret is required when plus.enabled is true" .Values.plus.controlPlane.sharedSecret.existingSecret -}}
{{- $plus := dict "enabled" true "controlPlane" (dict "host" "0.0.0.0" "port" (int .Values.plus.controlPlane.port) "sharedSecretEnv" "TITANIUM_CONTROL_SECRET") -}}
{{- if .Values.plus.options -}}
{{- $_ := set $plus "options" .Values.plus.options -}}
{{- end -}}
{{- $_ := set $cfg "plus" $plus -}}
{{- end -}}
{{- toYaml $cfg -}}
{{- end }}
