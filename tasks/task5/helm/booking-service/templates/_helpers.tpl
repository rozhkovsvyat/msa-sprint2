{{- define "booking-service.labels" -}}
app: {{ .Values.appName }}
version: {{ .Values.version }}
app.kubernetes.io/name: {{ .Values.appName }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/version: {{ .Values.image.tag | default .Chart.AppVersion | quote }}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version }}
{{- end }}

{{/* Deployment каждой версии выбирает свои поды; Service выбирает все версии по app — версии различает Istio (subsets) */}}
{{- define "booking-service.selectorLabels" -}}
app: {{ .Values.appName }}
version: {{ .Values.version }}
{{- end }}
