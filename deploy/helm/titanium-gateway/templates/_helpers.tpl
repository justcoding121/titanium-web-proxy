{{- define "titanium-gateway.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" }}
{{- end }}

{{- define "titanium-gateway.fullname" -}}
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

{{- define "titanium-gateway.chart" -}}
{{- printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" }}
{{- end }}

{{/* Names of the objects the controller and the data plane share. */}}
{{- define "titanium-gateway.controllerName" -}}
{{- printf "%s-controller" (include "titanium-gateway.fullname" .) | trunc 63 | trimSuffix "-" }}
{{- end }}

{{- define "titanium-gateway.dataName" -}}
{{- printf "%s-data" (include "titanium-gateway.fullname" .) | trunc 58 | trimSuffix "-" }}
{{- end }}

{{- define "titanium-gateway.tlsSecretName" -}}
{{- printf "%s-tls" (include "titanium-gateway.dataName" .) }}
{{- end }}

{{- define "titanium-gateway.adminSecretName" -}}
{{- default (printf "%s-admin" (include "titanium-gateway.fullname" .)) .Values.dataPlane.admin.token.existingSecret }}
{{- end }}

{{- define "titanium-gateway.commonLabels" -}}
helm.sh/chart: {{ include "titanium-gateway.chart" . }}
app.kubernetes.io/part-of: titanium-gateway
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end }}

{{- define "titanium-gateway.controllerSelectorLabels" -}}
app.kubernetes.io/name: {{ include "titanium-gateway.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/component: controller
{{- end }}

{{- define "titanium-gateway.dataSelectorLabels" -}}
app.kubernetes.io/name: {{ include "titanium-gateway.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/component: data-plane
{{- end }}

{{- define "titanium-gateway.image" -}}
{{- $image := .image -}}
{{- if $image.digest }}
{{- printf "%s@%s" $image.repository $image.digest }}
{{- else }}
{{- printf "%s:%s" $image.repository (default .root.Chart.AppVersion $image.tag) }}
{{- end }}
{{- end }}

{{/* "80=8080,443=8443": Gateway listener port to data-plane container port, from dataPlane.ports. */}}
{{- define "titanium-gateway.portMap" -}}
{{- $pairs := list -}}
{{- range .Values.dataPlane.ports -}}
{{- $pairs = append $pairs (printf "%d=%d" (int .servicePort) (int .containerPort)) -}}
{{- end -}}
{{- join "," $pairs -}}
{{- end }}
