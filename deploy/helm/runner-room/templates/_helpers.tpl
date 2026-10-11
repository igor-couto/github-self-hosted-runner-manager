{{- define "runner-room.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- define "runner-room.fullname" -}}
{{- default (printf "%s-%s" .Release.Name (include "runner-room.name" .)) .Values.fullnameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- define "runner-room.selectorLabels" -}}
app.kubernetes.io/name: {{ include "runner-room.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end -}}
{{- define "runner-room.labels" -}}
{{ include "runner-room.selectorLabels" . }}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end -}}
{{- define "runner-room.image" -}}
{{ printf "%s:%s" .Values.image.repository .Values.image.tag }}
{{- end -}}
{{- define "runner-room.claim" -}}
{{ default (include "runner-room.fullname" .) .Values.persistence.existingClaim }}
{{- end -}}
