# Synthetic lifecycle controls only. Real API/storage evidence is separate.
param([switch]$WorkloadEvidenceOnly)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'kubernetes-qualification-process.ps1')
. (Join-Path $PSScriptRoot 'kubernetes-qualification-evidence.ps1')
. (Join-Path $PSScriptRoot 'kubernetes-qualification-diagnostics.ps1')
# Frozen rev2 wire transcripts are synthetic controls, never runtime case evidence.
function Test-QualificationCallerJournalControls([string]$PrivateDirectory) {
    $journal=Join-Path $PrivateDirectory 'diagnostic-progress.txt'
    $normal=@'
1 durable-entry 3600000
2 catalog-init-start 3600000
3 catalog-init-completed 3600000
4 namespace-create-start 3600000
5 namespace-create-completed 3600000
6 durable-suite-start 3600000
7 workload-actions-start 3600000
8 accepted-setup-start 3600000
9 accepted-setup-completed 3600000
10 restore-start 3600000
11 restore-completed 3600000
12 accepted-setup-start 3600000
13 accepted-setup-completed 3600000
14 accepted-setup-start 3600000
15 accepted-setup-completed 3600000
16 accepted-setup-start 3600000
17 accepted-setup-completed 3600000
18 accepted-setup-start 3600000
19 accepted-setup-completed 3600000
20 accepted-setup-start 3600000
21 accepted-setup-completed 3600000
22 restore-start 3600000
23 restore-completed 3600000
24 accepted-setup-start 3600000
25 accepted-setup-completed 3600000
26 workload-actions-completed 3600000
27 identity-actions-start 3600000
28 runtime-setup-start 3600000
29 runtime-setup-completed 3600000
30 restore-start 3600000
31 restore-completed 3600000
32 runtime-setup-start 3600000
33 runtime-setup-completed 3600000
34 runtime-setup-start 3600000
35 runtime-setup-completed 3600000
36 restore-start 3600000
37 restore-completed 3600000
38 runtime-setup-start 3600000
39 runtime-setup-completed 3600000
40 runtime-setup-start 3600000
41 runtime-setup-completed 3600000
42 runtime-setup-start 3600000
43 runtime-setup-completed 3600000
44 runtime-setup-start 3600000
45 runtime-setup-completed 3600000
46 runtime-setup-start 3600000
47 runtime-setup-completed 3600000
48 identity-actions-completed 3600000
49 runtime-auth-start 3600000
50 runtime-auth-completed 3600000
51 runtime-final-revoke-start 3600000
52 runtime-final-revoke-completed 3600000
53 runtime-final-history-start 3600000
54 runtime-final-history-completed 3600000
55 runtime-final-authorization-start 3600000
56 runtime-final-authorization-completed 3600000
57 runtime-candidate-dispose-start 3600000
58 runtime-candidate-dispose-completed 3600000
59 initial-delivery-start 3600000
60 delivery-provenance-start 3600000
61 delivery-provenance-completed 3600000
62 delivery-pod-start 3600000
63 delivery-pod-completed 3600000
64 delivery-token-start 3600000
65 delivery-token-completed 3600000
66 delivery-host-first-start 3600000
67 delivery-host-first-built 3600000
68 delivery-host-first-credential-ready 3600000
69 delivery-host-first-started 3600000
70 delivery-first-request-start 3600000
71 delivery-first-request-completed 3600000
72 delivery-after-state-start 3600000
73 delivery-after-state-completed 3600000
74 delivery-result-check-start 3600000
75 delivery-result-check-completed 3600000
76 delivery-review-refusal-start 3600000
77 delivery-review-refusal-completed 3600000
78 delivery-review-republish-start 3600000
79 delivery-review-republish-completed 3600000
80 delivery-authority-call-start 3600000
81 delivery-authority-call-completed 3600000
82 delivery-authority-refusal-start 3600000
83 delivery-authority-refusal-completed 3600000
84 delivery-authority-republish-start 3600000
85 delivery-authority-republish-completed 3600000
86 delivery-startup-start 3600000
87 startup-pod-start 3600000
88 startup-pod-completed 3600000
89 startup-token-start 3600000
90 startup-token-completed 3600000
91 startup-file-start 3600000
92 startup-file-completed 3600000
93 startup-fingerprints-start 3600000
94 startup-fingerprints-completed 3600000
95 startup-before-state-start 3600000
96 startup-before-state-completed 3600000
97 startup-first-fetch-start 3600000
98 startup-first-fetch-completed 3600000
99 startup-after-state-start 3600000
100 startup-after-state-completed 3600000
101 startup-restart-fetch-start 3600000
102 startup-restart-fetch-completed 3600000
103 startup-revoke-start 3600000
104 startup-revoke-completed 3600000
105 startup-denial-probe-start 3600000
106 startup-denial-probe-completed 3600000
107 startup-republish-start 3600000
108 startup-republish-completed 3600000
109 startup-pod-cleanup-start 3600000
110 startup-pod-cleanup-completed 3600000
111 startup-directory-cleanup-start 3600000
112 startup-directory-cleanup-completed 3600000
113 delivery-startup-completed 3600000
114 delivery-host-first-dispose-start 3600000
115 delivery-host-stop-start 3600000
116 delivery-host-stop-completed 3600000
117 delivery-host-dispose-start 3600000
118 delivery-host-dispose-completed 3600000
119 delivery-host-first-dispose-completed 3600000
120 delivery-host-restart-start 3600000
121 delivery-host-restart-built 3600000
122 delivery-host-restart-credential-ready 3600000
123 delivery-host-restart-started 3600000
124 delivery-first-request-start 3600000
125 delivery-first-request-completed 3600000
126 delivery-after-state-start 3600000
127 delivery-after-state-completed 3600000
128 delivery-result-check-start 3600000
129 delivery-result-check-completed 3600000
130 delivery-host-restart-dispose-start 3600000
131 delivery-host-stop-start 3600000
132 delivery-host-stop-completed 3600000
133 delivery-host-dispose-start 3600000
134 delivery-host-dispose-completed 3600000
135 delivery-host-restart-dispose-completed 3600000
136 delivery-catalog-verify-start 3600000
137 delivery-catalog-verify-completed 3600000
138 delivery-pod-cleanup-start 3600000
139 delivery-pod-cleanup-completed 3600000
140 runtime-auth-start 3600000
141 runtime-auth-completed 3600000
142 runtime-final-revoke-start 3600000
143 runtime-final-revoke-completed 3600000
144 runtime-final-history-start 3600000
145 runtime-final-history-completed 3600000
146 runtime-final-authorization-start 3600000
147 runtime-final-authorization-completed 3600000
148 runtime-candidate-dispose-start 3600000
149 runtime-candidate-dispose-completed 3600000
150 mixed-delivery-start 3600000
151 mixed-baseline-prepared 3600000
152 mixed-preparation-completed 3600000
153 mixed-documents-completed 3600000
154 mixed-identity-completed 3600000
155 mixed-publication-completed 3600000
156 delivery-provenance-start 3600000
157 delivery-provenance-completed 3600000
158 delivery-pod-start 3600000
159 delivery-pod-completed 3600000
160 delivery-token-start 3600000
161 delivery-token-completed 3600000
162 delivery-host-first-start 3600000
163 delivery-host-first-built 3600000
164 delivery-host-first-credential-ready 3600000
165 delivery-host-first-started 3600000
166 delivery-first-request-start 3600000
167 delivery-first-request-completed 3600000
168 delivery-after-state-start 3600000
169 delivery-after-state-completed 3600000
170 delivery-result-check-start 3600000
171 delivery-result-check-completed 3600000
172 delivery-review-refusal-start 3600000
173 delivery-review-refusal-completed 3600000
174 delivery-review-republish-start 3600000
175 delivery-review-republish-completed 3600000
176 delivery-authority-call-start 3600000
177 delivery-authority-call-completed 3600000
178 delivery-authority-refusal-start 3600000
179 delivery-authority-refusal-completed 3600000
180 delivery-authority-republish-start 3600000
181 delivery-authority-republish-completed 3600000
182 delivery-host-first-dispose-start 3600000
183 delivery-host-stop-start 3600000
184 delivery-host-stop-completed 3600000
185 delivery-host-dispose-start 3600000
186 delivery-host-dispose-completed 3600000
187 delivery-host-first-dispose-completed 3600000
188 delivery-host-restart-start 3600000
189 delivery-host-restart-built 3600000
190 delivery-host-restart-credential-ready 3600000
191 delivery-host-restart-started 3600000
192 delivery-first-request-start 3600000
193 delivery-first-request-completed 3600000
194 delivery-after-state-start 3600000
195 delivery-after-state-completed 3600000
196 delivery-result-check-start 3600000
197 delivery-result-check-completed 3600000
198 delivery-host-restart-dispose-start 3600000
199 delivery-host-stop-start 3600000
200 delivery-host-stop-completed 3600000
201 delivery-host-dispose-start 3600000
202 delivery-host-dispose-completed 3600000
203 delivery-host-restart-dispose-completed 3600000
204 delivery-catalog-verify-start 3600000
205 delivery-catalog-verify-completed 3600000
206 delivery-pod-cleanup-start 3600000
207 delivery-pod-cleanup-completed 3600000
208 mixed-delivery-completed 3600000
209 inner-cleanup-start 3600000
210 namespace-cleanup-start 3600000
211 namespace-absence-confirmed 3600000
212 catalog-dispose-start 3600000
213 catalog-dispose-completed 3600000
214 inner-cleanup-completed 3600000
'@
    $normal=$normal.Replace("`r`n","`n")+"`n"
    $exceptional=@'
1 durable-entry 3600000
2 catalog-init-start 3600000
3 catalog-init-completed 3600000
4 namespace-create-start 3600000
5 namespace-create-completed 3600000
6 durable-suite-start 3600000
7 workload-actions-start 3600000
8 accepted-setup-start 3600000
9 accepted-setup-completed 3600000
10 restore-start 3600000
11 restore-completed 3600000
12 accepted-setup-start 3600000
13 accepted-setup-completed 3600000
14 accepted-setup-start 3600000
15 accepted-setup-completed 3600000
16 accepted-setup-start 3600000
17 accepted-setup-completed 3600000
18 accepted-setup-start 3600000
19 accepted-setup-completed 3600000
20 accepted-setup-start 3600000
21 accepted-setup-completed 3600000
22 restore-start 3600000
23 restore-completed 3600000
24 accepted-setup-start 3600000
25 accepted-setup-completed 3600000
26 workload-actions-completed 3600000
27 identity-actions-start 3600000
28 runtime-setup-start 3600000
29 runtime-setup-completed 3600000
30 restore-start 3600000
31 restore-completed 3600000
32 runtime-setup-start 3600000
33 runtime-setup-completed 3600000
34 runtime-setup-start 3600000
35 runtime-setup-completed 3600000
36 restore-start 3600000
37 restore-completed 3600000
38 runtime-setup-start 3600000
39 runtime-setup-completed 3600000
40 runtime-setup-start 3600000
41 runtime-setup-completed 3600000
42 runtime-setup-start 3600000
43 runtime-setup-completed 3600000
44 runtime-setup-start 3600000
45 runtime-setup-completed 3600000
46 runtime-setup-start 3600000
47 runtime-setup-completed 3600000
48 identity-actions-completed 3600000
49 runtime-auth-start 3600000
50 runtime-auth-completed 3600000
51 runtime-final-revoke-start 3600000
52 runtime-final-revoke-completed 3600000
53 runtime-final-history-start 3600000
54 runtime-final-history-completed 3600000
55 runtime-final-authorization-start 3600000
56 runtime-final-authorization-completed 3600000
57 runtime-candidate-dispose-start 3600000
58 runtime-candidate-dispose-completed 3600000
59 initial-delivery-start 3600000
60 delivery-provenance-start 3600000
61 delivery-provenance-completed 3600000
62 delivery-pod-start 3600000
63 delivery-pod-completed 3600000
64 delivery-token-start 3600000
65 delivery-token-completed 3600000
66 delivery-host-first-start 3600000
67 delivery-host-first-built 3600000
68 delivery-host-first-credential-ready 3600000
69 delivery-host-first-started 3600000
70 delivery-first-request-start 3600000
71 delivery-first-request-completed 3600000
72 delivery-after-state-start 3600000
73 delivery-after-state-completed 3600000
74 delivery-result-check-start 3600000
75 delivery-result-check-completed 3600000
76 delivery-review-refusal-start 3600000
77 delivery-review-refusal-completed 3600000
78 delivery-review-republish-start 3600000
79 delivery-review-republish-completed 3600000
80 delivery-authority-call-start 3600000
81 delivery-authority-call-completed 3600000
82 delivery-authority-refusal-start 3600000
83 delivery-authority-refusal-completed 3600000
84 delivery-authority-republish-start 3600000
85 delivery-authority-republish-completed 3600000
86 delivery-startup-start 3600000
87 startup-pod-start 3600000
88 startup-pod-completed 3600000
89 startup-token-start 3600000
90 startup-token-completed 3600000
91 startup-file-start 3600000
92 startup-file-completed 3600000
93 startup-fingerprints-start 3600000
94 startup-fingerprints-completed 3600000
95 startup-before-state-start 3600000
96 startup-before-state-completed 3600000
97 startup-first-fetch-start 3600000
98 startup-first-fetch-completed 3600000
99 startup-after-state-start 3600000
100 startup-after-state-completed 3600000
101 startup-restart-fetch-start 3600000
102 startup-restart-fetch-completed 3600000
103 startup-revoke-start 3600000
104 startup-revoke-completed 3600000
105 startup-denial-probe-start 3600000
106 startup-denial-probe-completed 3600000
107 startup-republish-start 3600000
108 startup-republish-completed 3600000
109 startup-pod-cleanup-start 3600000
110 startup-pod-cleanup-completed 3600000
111 startup-directory-cleanup-start 3600000
112 startup-directory-cleanup-completed 3600000
113 delivery-startup-completed 3600000
114 delivery-host-first-dispose-start 3600000
115 delivery-host-stop-start 3600000
116 delivery-host-stop-completed 3600000
117 delivery-host-dispose-start 3600000
118 delivery-host-dispose-completed 3600000
119 delivery-host-first-dispose-completed 3600000
120 delivery-host-restart-start 3600000
121 delivery-host-restart-built 3600000
122 delivery-host-restart-credential-ready 3600000
123 delivery-host-restart-started 3600000
124 delivery-first-request-start 3600000
125 delivery-first-request-completed 3600000
126 delivery-after-state-start 3600000
127 delivery-after-state-completed 3600000
128 delivery-result-check-start 3600000
129 delivery-result-check-completed 3600000
130 delivery-host-restart-dispose-start 3600000
131 delivery-host-stop-start 3600000
132 delivery-host-stop-completed 3600000
133 delivery-host-dispose-start 3600000
134 delivery-host-dispose-completed 3600000
135 delivery-host-restart-dispose-completed 3600000
136 delivery-catalog-verify-start 3600000
137 delivery-catalog-verify-completed 3600000
138 delivery-pod-cleanup-start 3600000
139 delivery-pod-cleanup-completed 3600000
140 runtime-auth-start 3600000
141 runtime-auth-completed 3600000
142 runtime-final-revoke-start 3600000
143 runtime-final-revoke-completed 3600000
144 runtime-final-history-start 3600000
145 runtime-final-history-completed 3600000
146 runtime-final-authorization-start 3600000
147 runtime-final-authorization-completed 3600000
148 runtime-candidate-dispose-start 3600000
149 runtime-candidate-dispose-completed 3600000
150 mixed-delivery-start 3600000
151 mixed-baseline-prepared 3600000
152 mixed-preparation-completed 3600000
153 mixed-documents-completed 3600000
154 mixed-identity-completed 3600000
155 mixed-publication-completed 3600000
156 delivery-provenance-start 3600000
157 delivery-provenance-completed 3600000
158 delivery-pod-start 3600000
159 delivery-pod-completed 3600000
160 delivery-token-start 3600000
161 delivery-token-completed 3600000
162 delivery-host-first-start 3600000
163 delivery-host-first-built 3600000
164 delivery-host-first-credential-ready 3600000
165 delivery-host-first-started 3600000
166 delivery-first-request-start 3600000
167 delivery-first-request-completed 3600000
168 delivery-after-state-start 3600000
169 delivery-after-state-completed 3600000
170 delivery-result-check-start 3600000
171 delivery-result-check-completed 3600000
172 delivery-review-refusal-start 3600000
173 delivery-review-refusal-completed 3600000
174 delivery-review-republish-start 3600000
175 delivery-review-republish-completed 3600000
176 delivery-authority-call-start 3600000
177 delivery-authority-call-completed 3600000
178 delivery-authority-refusal-start 3600000
179 delivery-authority-refusal-completed 3600000
180 delivery-authority-republish-start 3600000
181 delivery-authority-republish-completed 3600000
182 delivery-host-first-dispose-start 3600000
183 delivery-host-stop-start 3600000
184 delivery-host-stop-completed 3600000
185 delivery-host-dispose-start 3600000
186 delivery-host-dispose-completed 3600000
187 delivery-host-first-dispose-completed 3600000
188 delivery-host-restart-start 3600000
189 delivery-host-restart-built 3600000
190 delivery-host-restart-credential-ready 3600000
191 delivery-host-restart-started 3600000
192 delivery-first-request-start 3600000
193 delivery-first-request-completed 3600000
194 delivery-after-state-start 3600000
195 delivery-after-state-completed 3600000
196 delivery-result-check-start 3600000
197 delivery-result-check-completed 3600000
198 delivery-host-restart-dispose-start 3600000
199 delivery-host-stop-start 3600000
200 delivery-host-stop-completed 3600000
201 delivery-host-dispose-start 3600000
202 delivery-host-dispose-completed 3600000
203 delivery-host-restart-dispose-completed 3600000
204 delivery-catalog-verify-start 3600000
205 delivery-catalog-verify-completed 3600000
206 delivery-pod-cleanup-start 3600000
207 delivery-pod-cleanup-completed 3600000
208 mixed-delivery-completed 3600000
209 inner-cleanup-start 3600000
210 namespace-cleanup-start 3600000
211 namespace-absence-confirmed 3600000
212 catalog-dispose-start 3600000
213 catalog-dispose-completed 3600000
214 catalog-dispose-failed 3600000
215 inner-cleanup-failed 3600000
'@
    $exceptional=$exceptional.Replace("`r`n","`n")+"`n"
    $cancellation=@'
1 durable-entry 3600000
2 catalog-init-start 3600000
3 catalog-init-completed 3600000
4 namespace-create-start 3600000
5 namespace-create-completed 3600000
6 durable-suite-start 3600000
7 workload-actions-start 3600000
8 accepted-setup-start 3600000
9 accepted-setup-completed 3600000
10 restore-start 3600000
11 restore-completed 3600000
12 accepted-setup-start 3600000
13 accepted-setup-completed 3600000
14 accepted-setup-start 3600000
15 accepted-setup-completed 3600000
16 accepted-setup-start 3600000
17 accepted-setup-completed 3600000
18 accepted-setup-start 3600000
19 accepted-setup-completed 3600000
20 accepted-setup-start 3600000
21 accepted-setup-completed 3600000
22 restore-start 3600000
23 restore-completed 3600000
24 accepted-setup-start 3600000
25 accepted-setup-completed 3600000
26 workload-actions-completed 3600000
27 identity-actions-start 3600000
28 runtime-setup-start 3600000
29 runtime-setup-completed 3600000
30 restore-start 3600000
31 restore-completed 3600000
32 runtime-setup-start 3600000
33 runtime-setup-completed 3600000
34 runtime-setup-start 3600000
35 runtime-setup-completed 3600000
36 restore-start 3600000
37 restore-completed 3600000
38 runtime-setup-start 3600000
39 runtime-setup-completed 3600000
40 runtime-setup-start 3600000
41 runtime-setup-completed 3600000
42 runtime-setup-start 3600000
43 runtime-setup-completed 3600000
44 runtime-setup-start 3600000
45 runtime-setup-completed 3600000
46 runtime-setup-start 3600000
47 runtime-setup-completed 3600000
48 identity-actions-completed 3600000
49 runtime-auth-start 3600000
50 runtime-auth-completed 3600000
51 runtime-final-revoke-start 3600000
52 runtime-final-revoke-completed 3600000
53 runtime-final-history-start 3600000
54 runtime-final-history-completed 3600000
55 runtime-final-authorization-start 3600000
56 runtime-final-authorization-completed 3600000
57 runtime-candidate-dispose-start 3600000
58 runtime-candidate-dispose-completed 3600000
59 initial-delivery-start 3600000
60 delivery-provenance-start 3600000
61 delivery-provenance-completed 3600000
62 delivery-pod-start 3600000
63 delivery-pod-completed 3600000
64 delivery-token-start 3600000
65 delivery-token-completed 3600000
66 delivery-host-first-start 3600000
67 delivery-host-first-built 3600000
68 delivery-host-first-credential-ready 3600000
69 delivery-host-first-started 3600000
70 delivery-first-request-start 3600000
71 delivery-first-request-completed 3600000
72 delivery-after-state-start 3600000
73 delivery-after-state-completed 3600000
74 delivery-result-check-start 3600000
75 delivery-result-check-completed 3600000
76 delivery-review-refusal-start 3600000
77 delivery-review-refusal-completed 3600000
78 delivery-review-republish-start 3600000
79 delivery-review-republish-completed 3600000
80 delivery-authority-call-start 3600000
81 delivery-authority-call-completed 3600000
82 delivery-authority-refusal-start 3600000
83 delivery-authority-refusal-completed 3600000
84 delivery-authority-republish-start 3600000
85 delivery-authority-republish-completed 3600000
86 delivery-startup-start 3600000
87 startup-pod-start 3600000
88 startup-pod-completed 3600000
89 startup-token-start 3600000
90 startup-token-completed 3600000
91 startup-file-start 3600000
92 startup-file-completed 3600000
93 startup-fingerprints-start 3600000
94 startup-fingerprints-completed 3600000
95 startup-before-state-start 3600000
96 startup-before-state-completed 3600000
97 startup-first-fetch-start 3600000
98 startup-first-fetch-failed 3600000
99 startup-pod-cleanup-start 3600000
100 startup-pod-cleanup-completed 3600000
101 startup-directory-cleanup-start 3600000
102 startup-directory-cleanup-completed 3600000
103 delivery-startup-failed 3600000
104 delivery-host-first-dispose-start 3600000
105 delivery-host-dispose-start 3600000
106 delivery-host-dispose-completed 3600000
107 delivery-host-first-dispose-completed 3600000
108 delivery-pod-cleanup-start 3600000
109 delivery-pod-cleanup-completed 3600000
110 runtime-candidate-dispose-start 3600000
111 runtime-candidate-dispose-completed 3600000
112 inner-cleanup-start 3600000
113 namespace-cleanup-start 3600000
114 namespace-absence-confirmed 3600000
115 catalog-dispose-start 3600000
116 catalog-dispose-completed 3600000
117 catalog-dispose-failed 3600000
118 inner-cleanup-failed 3600000
'@
    $cancellation=$cancellation.Replace("`r`n","`n")+"`n"
    $cancellationValid=@'
1 durable-entry 3600000
2 catalog-init-start 3600000
3 catalog-init-completed 3600000
4 namespace-create-start 3600000
5 namespace-create-completed 3600000
6 durable-suite-start 3600000
7 workload-actions-start 3600000
8 accepted-setup-start 3600000
9 accepted-setup-completed 3600000
10 restore-start 3600000
11 restore-completed 3600000
12 accepted-setup-start 3600000
13 accepted-setup-completed 3600000
14 accepted-setup-start 3600000
15 accepted-setup-completed 3600000
16 accepted-setup-start 3600000
17 accepted-setup-completed 3600000
18 accepted-setup-start 3600000
19 accepted-setup-completed 3600000
20 accepted-setup-start 3600000
21 accepted-setup-completed 3600000
22 restore-start 3600000
23 restore-completed 3600000
24 accepted-setup-start 3600000
25 accepted-setup-completed 3600000
26 workload-actions-completed 3600000
27 identity-actions-start 3600000
28 runtime-setup-start 3600000
29 runtime-setup-completed 3600000
30 restore-start 3600000
31 restore-completed 3600000
32 runtime-setup-start 3600000
33 runtime-setup-completed 3600000
34 runtime-setup-start 3600000
35 runtime-setup-completed 3600000
36 restore-start 3600000
37 restore-completed 3600000
38 runtime-setup-start 3600000
39 runtime-setup-completed 3600000
40 runtime-setup-start 3600000
41 runtime-setup-completed 3600000
42 runtime-setup-start 3600000
43 runtime-setup-completed 3600000
44 runtime-setup-start 3600000
45 runtime-setup-completed 3600000
46 runtime-setup-start 3600000
47 runtime-setup-completed 3600000
48 identity-actions-completed 3600000
49 runtime-auth-start 3600000
50 runtime-auth-completed 3600000
51 runtime-final-revoke-start 3600000
52 runtime-final-revoke-completed 3600000
53 runtime-final-history-start 3600000
54 runtime-final-history-completed 3600000
55 runtime-final-authorization-start 3600000
56 runtime-final-authorization-completed 3600000
57 runtime-candidate-dispose-start 3600000
58 runtime-candidate-dispose-completed 3600000
59 initial-delivery-start 3600000
60 delivery-provenance-start 3600000
61 delivery-provenance-completed 3600000
62 delivery-pod-start 3600000
63 delivery-pod-completed 3600000
64 delivery-token-start 3600000
65 delivery-token-completed 3600000
66 delivery-host-first-start 3600000
67 delivery-host-first-built 3600000
68 delivery-host-first-credential-ready 3600000
69 delivery-host-first-started 3600000
70 delivery-first-request-start 3600000
71 delivery-first-request-completed 3600000
72 delivery-after-state-start 3600000
73 delivery-after-state-completed 3600000
74 delivery-result-check-start 3600000
75 delivery-result-check-completed 3600000
76 delivery-review-refusal-start 3600000
77 delivery-review-refusal-completed 3600000
78 delivery-review-republish-start 3600000
79 delivery-review-republish-completed 3600000
80 delivery-authority-call-start 3600000
81 delivery-authority-call-completed 3600000
82 delivery-authority-refusal-start 3600000
83 delivery-authority-refusal-completed 3600000
84 delivery-authority-republish-start 3600000
85 delivery-authority-republish-completed 3600000
86 delivery-startup-start 3600000
87 startup-pod-start 3600000
88 startup-pod-completed 3600000
89 startup-token-start 3600000
90 startup-token-completed 3600000
91 startup-file-start 3600000
92 startup-file-completed 3600000
93 startup-fingerprints-start 3600000
94 startup-fingerprints-completed 3600000
95 startup-before-state-start 3600000
96 startup-before-state-completed 3600000
97 startup-first-fetch-start 3600000
98 startup-first-fetch-failed 3600000
99 startup-pod-cleanup-start 3600000
100 startup-pod-cleanup-completed 3600000
101 startup-directory-cleanup-start 3600000
102 startup-directory-cleanup-completed 3600000
103 delivery-startup-failed 3600000
104 delivery-host-first-dispose-start 3600000
105 delivery-host-dispose-start 3600000
106 delivery-host-dispose-completed 3600000
107 delivery-host-first-dispose-completed 3600000
108 delivery-pod-cleanup-start 3600000
109 delivery-pod-cleanup-completed 3600000
110 runtime-candidate-dispose-start 3600000
111 runtime-candidate-dispose-completed 3600000
112 inner-cleanup-start 3600000
113 namespace-cleanup-start 3600000
114 namespace-absence-confirmed 3600000
115 catalog-dispose-start 3600000
116 catalog-dispose-completed 3600000
117 inner-cleanup-completed 3600000
'@
    $cancellationValid=$cancellationValid.Replace("`r`n","`n")+"`n"
    foreach($case in @(
        @{Name='N';Text=$normal;Count=214;Bytes=8344;Hash='61082729674b86c6c63b8f73c72fd1b7b1c3f9055a4f03f76ccb06865bca5539';Status='valid'},
        @{Name='E';Text=$exceptional;Count=215;Bytes=8376;Hash='f99aa3759ec5855e7884a3532417fc055a9657f334c76a742e042536577e2213';Status='invalid'},
        @{Name='C';Text=$cancellation;Count=118;Bytes=4328;Hash='daa75b9bb0c99fbe921f538ac5012138288459a2c9ac98ab3a0c051648bcefa7';Status='invalid'},
        @{Name='C-valid';Text=$cancellationValid;Count=117;Bytes=4296;Hash='899f84814e5f94dd62cc67df9ffe72f58698eb2a74d13be8dd8d4f39dbe6d0ae';Status='valid'}
    )){
        $wire=[Text.Encoding]::ASCII.GetBytes($case.Text)
        $hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($wire)).ToLowerInvariant()
        if($wire.Length -ne $case.Bytes -or ($case.Text.Split("`n").Length-1) -ne $case.Count -or $hash -cne $case.Hash){throw ('Frozen transcript changed: '+$case.Name)}
        [IO.File]::WriteAllBytes($journal,$wire)
        $parsed=Read-QualificationJournal $PrivateDirectory
        $output=@(Format-QualificationJournal $parsed)
        if($parsed.Status -cne $case.Status){throw ('Caller transcript classification: '+$case.Name+' expected '+$case.Status+' actual '+$parsed.Status)}
        if($parsed.Status -ceq 'valid' -and $parsed.Records.Count -ne $case.Count){throw 'Validated caller records lost'}
        if($output -match '^PASS |^Harness: PASS$|protected-'){throw 'Caller progress became gate success or leaked payload'}
        if($parsed.Status -ceq 'invalid' -and ($parsed.Records.Count -ne 0 -or 'Diagnostic inner namespace: unknown' -cnotin $output -or 'Diagnostic inner catalog: unknown' -cnotin $output)){throw 'Invalid complete contradiction was salvaged'}
        Write-Output ('PASS frozen caller transcript '+$case.Name+' (synthetic)')
    }
    $normalLines=$normal.TrimEnd("`n").Split("`n")
    $prefix=($normalLines[0..96] -join "`n")+"`n"
    foreach($case in @(
        @{Text=($prefix+'98 startup-fi');Status='partial-prefix';Records=97},
        @{Text=($prefix+"98 startup-fi`n");Status='invalid';Records=0},
        @{Text=($prefix.Replace('1 durable-entry','1 protected-sentinel')+'98 startup-fi');Status='invalid';Records=0},
        @{Text=($prefix+'98 protected-final-sentinel');Status='partial-prefix';Records=97},
        @{Text=($prefix+"98 startup-first-fetch-completed 3600000`n99 startup-first-fetch-failed 3600000`n");Status='invalid';Records=0},
        @{Text=$normal.Replace('delivery-after-state-start','startup-before-state-start');Status='invalid';Records=0},
        @{Text=$normal.Replace('delivery-review-republish-completed','delivery-review-refusal-completed');Status='invalid';Records=0},
        @{Text=$normal.Replace('startup-denial-probe-completed','startup-first-fetch-completed');Status='invalid';Records=0}
    )){
        [IO.File]::WriteAllText($journal,$case.Text,[Text.UTF8Encoding]::new($false))
        $parsed=Read-QualificationJournal $PrivateDirectory
        $output=@(Format-QualificationJournal $parsed)
        if($parsed.Status -cne $case.Status -or $parsed.Records.Count -ne $case.Records -or $output -match 'protected-|^PASS |^Harness: PASS$'){throw 'Partial prefix, complete-record/context rejection or privacy changed'}
        if($case.Status -ceq 'partial-prefix' -and ('Diagnostic journal incomplete: final unterminated fragment discarded; FAILURE' -cnotin $output -or 'Diagnostic progress: 97 startup-first-fetch-start 3600000' -cnotin $output)){throw 'Torn fragment prefix was lost or promoted to success'}
        if('Diagnostic inner namespace: unknown' -cnotin $output -or 'Diagnostic inner catalog: unknown' -cnotin $output){throw 'Partial or invalid journal claimed cleanup'}
    }
    # Caller cancellation reaches independent cleanup; a late server terminal emits nothing.
    $callerIds=@($cancellationValid.TrimEnd("`n").Split("`n") | ForEach-Object {($_ -split ' ')[1]})
    if('startup-first-fetch-failed' -cnotin $callerIds -or 'startup-directory-cleanup-completed' -cnotin $callerIds -or 'delivery-host-dispose-completed' -cnotin $callerIds){throw 'Cancellation cleanup control inventory changed'}
    # A failed stop still enters disposal; a failed Pod cleanup still enters directory cleanup.
    foreach($text in @(
        $normal.Replace('delivery-host-stop-completed','delivery-host-stop-failed'),
        $cancellationValid.Replace('startup-pod-cleanup-completed','startup-pod-cleanup-failed')
    )){
        # Stop failure cannot continue to restart or later body work: retain only reachable cleanup.
        if($text -cmatch 'delivery-host-stop-failed'){
            $ids=@($text.TrimEnd("`n").Split("`n") | ForEach-Object {($_ -split ' ')[1]})
            $cut=[Array]::IndexOf($ids,'delivery-host-first-dispose-completed')
            $ids=$ids[0..$cut]+@('delivery-pod-cleanup-start','delivery-pod-cleanup-completed','inner-cleanup-start','namespace-cleanup-start','namespace-absence-confirmed','catalog-dispose-start','catalog-dispose-completed','inner-cleanup-completed')
            $text=(@(for($i=0;$i -lt $ids.Count;$i++){($i+1).ToString()+' '+$ids[$i]+' 3600000'}) -join "`n")+"`n"
        }
        [IO.File]::WriteAllText($journal,$text)
        if((Read-QualificationJournal $PrivateDirectory).Status -cne 'valid'){throw 'Independent cleanup after caller failure rejected'}
    }
    [IO.File]::WriteAllBytes($journal,[byte[]](49,32,100,117,114,97,98,108,101,45,101,110,116,114,121,32,48,10,255))
    if((Read-QualificationJournal $PrivateDirectory).Status -cne 'invalid'){throw 'Non-ASCII torn fragment accepted'}
    # Compile the actual journal source in an isolated control assembly; no cluster or Host runs.
    $writerSource=[IO.File]::ReadAllText((Join-Path $PSScriptRoot '../tools/Haby.Kubernetes.Qualification/QualificationDiagnosticJournal.cs'))
    $deliverySource=[IO.File]::ReadAllText((Join-Path $PSScriptRoot '../tools/Haby.Kubernetes.Qualification/RuntimeDeliveryCases.cs'))
    $bindings=@([regex]::Matches($writerSource,'QualificationMilestone\.([A-Za-z]+) => "((?:delivery|startup)-[a-z-]+)"') | Where-Object {$_.Groups[2].Value -cmatch '^(?:delivery-(?:after-state|result-check|review-refusal|review-republish|authority-call|authority-refusal|authority-republish|startup|host-stop|host-dispose|catalog-verify)|startup-(?:pod|token|file|fingerprints|before-state|first-fetch|after-state|restart-fetch|revoke|denial-probe|republish|pod-cleanup|directory-cleanup))-(?:start|completed|failed)$'})
    if($bindings.Count -ne 72 -or ([regex]::Matches($deliverySource,'diagnostics\?\.Begin\(')).Count -ne 24){throw 'Closed writer/caller manifest changed'}
    $ids=@($bindings | ForEach-Object {$_.Groups[2].Value})
    $manifestHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::ASCII.GetBytes(($ids -join "`n")+"`n"))).ToLowerInvariant()
    if($manifestHash -cne '1d871312d1ba0cc7c3fde9a60964b250454176605f0426fd80fb0c86b7d3aaf0'){throw 'Closed 72-ID manifest digest changed'}
    foreach($range in @(
        @('        async Task Revoke()','        void Check('),
        @('                        afterAuthority = async () =>','                        var startup = new RuntimeStartupClient(client);'),
        @('    private sealed class ReviewBoundary','    private sealed class ReadBoundary'),
        @('    private sealed class ReadBoundary',$null)
    )){
        $start=$deliverySource.IndexOf($range[0],[StringComparison]::Ordinal)
        $end=if($range[1]){$deliverySource.IndexOf($range[1],$start,[StringComparison]::Ordinal)}else{$deliverySource.Length}
        if($start -lt 0 -or $end -le $start -or $deliverySource.Substring($start,$end-$start) -cmatch 'diagnostics|QualificationMilestone|Progress'){throw 'Server-hook diagnostic writer introduced'}
    }
    $controlSource=@'
public static class DiagnosticWriterControls
{
    public static void Run(string directory, string[] names)
    {
        string path = Path.Combine(directory, "diagnostic-progress.txt");
        File.WriteAllText(path, "");
        using (var journal = new QualificationDiagnosticJournal(directory))
        {
            foreach (string name in names) journal.Mark(Enum.Parse<QualificationMilestone>(name));
            if (journal.Failed) throw new InvalidOperationException("Closed writer IDs failed.");
        }
        if (File.ReadAllLines(path).Length != 72) throw new InvalidOperationException("Closed ID inventory lost.");
        File.WriteAllText(path, "");
        using (var journal = new QualificationDiagnosticJournal(directory))
        {
            using (var span = journal.Begin(QualificationMilestone.DeliveryAfterStateStart, QualificationMilestone.DeliveryAfterStateCompleted, QualificationMilestone.DeliveryAfterStateFailed))
            { span.Complete(); span.Complete(); }
            var primary = new OperationCanceledException("protected-writer-sentinel");
            try
            {
                using var span = journal.Begin(QualificationMilestone.StartupFirstFetchStart, QualificationMilestone.StartupFirstFetchCompleted, QualificationMilestone.StartupFirstFetchFailed);
                throw primary;
            }
            catch (OperationCanceledException caught)
            { if (!ReferenceEquals(primary, caught)) throw new InvalidOperationException("Primary replaced."); }
            using (var cleanup = journal.Begin(QualificationMilestone.StartupDirectoryCleanupStart, QualificationMilestone.StartupDirectoryCleanupCompleted, QualificationMilestone.StartupDirectoryCleanupFailed))
            { cleanup.Complete(); }
            if (journal.Failed) throw new InvalidOperationException("Caller scope writer failed.");
        }
        string[] records = File.ReadAllLines(path);
        string[] expected = { "delivery-after-state-start", "delivery-after-state-completed", "startup-first-fetch-start", "startup-first-fetch-failed", "startup-directory-cleanup-start", "startup-directory-cleanup-completed" };
        if (records.Length != expected.Length) throw new InvalidOperationException("Caller terminals repeated or lost.");
        for (int i = 0; i < records.Length; i++)
            if (records[i].Split(' ')[1] != expected[i]) throw new InvalidOperationException("Caller terminal order changed.");
        if (File.ReadAllText(path).Contains("protected-")) throw new InvalidOperationException("Exception payload leaked.");
        File.WriteAllText(path, "");
        using (var journal = new QualificationDiagnosticJournal(directory))
        {
            for (int i = 0; i < 256; i++) journal.Mark(QualificationMilestone.DurableEntry);
            if (journal.Failed) throw new InvalidOperationException("Writer cap decreased.");
            journal.Mark(QualificationMilestone.DurableEntry);
            if (!journal.Failed) throw new InvalidOperationException("Writer cap increased.");
        }
        if (File.ReadAllLines(path).Length != 256 || new FileInfo(path).Length > 16384)
            throw new InvalidOperationException("Writer overflow retained extra records.");
    }
}
'@
    Add-Type -TypeDefinition ("using System;`nusing System.IO;`n"+$writerSource+"`n"+$controlSource) -ErrorAction Stop
    [Haby.Kubernetes.Qualification.DiagnosticWriterControls]::Run($PrivateDirectory,[string[]]@($bindings | ForEach-Object {$_.Groups[1].Value}))
    Write-Output 'PASS actual closed-ID writer, terminal-once cancellation/cleanup, primary exception, 256-record cap and no hook writers'

    Write-Output 'PASS caller cancellation, opaque temporary hook fault, expected denial, independent cleanup and torn-prefix controls (synthetic)'
}

$launcher=Join-Path $PSScriptRoot 'run-kubernetes-qualification.ps1'
foreach($runtime in @('wslc','docker')){
$workloadScenarios=@('workload-safe-failure','workload-secret-failure','workload-invalid-failure','workload-oversized-failure','workload-invalid-success','workload-recorded-failure-success')
$scenarios=if($WorkloadEvidenceOnly){$workloadScenarios}else{@('success','elapsed-missing','elapsed-malformed','elapsed-out-of-range','build-failure','harness-nonzero','stock-marker','build-timeout','inspect-failure','stop-failure','remove-failure','foreign','evidence-failure','partial-launch','absent-launch','readiness-deadline','acl-failure','copy-failure','copy-ca-failure','copy-certificate-failure','copy-key-failure','copy-managed-failure','copy-invalid-exit','copy-primary-cleanup-failure','copy-timeout','harness-timeout','journal-invalid','journal-invalid-primary','journal-partial','journal-partial-primary','journal-partial-timeout')+$workloadScenarios}
foreach($scenario in $scenarios){
    $state=@{Name='';Private='';Removed=$false;Volumes=$true;Created=$false;Calls=@();TimedCalls=@();CopyCalls=0}
    $syntheticTimeout=$null
    $copyTimeout=if($scenario -ceq 'copy-timeout'){New-QualificationProcessFailure 'command-deadline' 42L}else{$null}
    $buildTimeout=if($scenario -ceq 'build-timeout'){New-QualificationProcessFailure 'command-deadline' 180001L}else{$null}
    if($scenario -in @('harness-timeout','journal-partial-timeout')){
        $syntheticTimeout=New-QualificationProcessFailure 'command-deadline' 180001
        $syntheticTimeoutDiagnostic=Get-QualificationProcessDiagnostic $syntheticTimeout
        if($syntheticTimeout.GetType() -ne [TimeoutException] -or $syntheticTimeoutDiagnostic.Kind -cne 'command-deadline' -or
            $syntheticTimeoutDiagnostic.ElapsedMilliseconds -isnot [long] -or $syntheticTimeoutDiagnostic.ElapsedMilliseconds -ne 180001){throw 'Synthetic timeout fixture construction failed'}
    }
    $runner={param($file,$arguments,$seconds)
        $state.Calls+=,$arguments
        if($file -ceq 'dotnet' -and $arguments[0] -cin @('build','run')){$state.TimedCalls+=,[pscustomobject]@{Arguments=$arguments;Seconds=$seconds}}
        $result=[pscustomobject]@{ExitCode=0;Output='';Error='';ElapsedMilliseconds=42L}
        if($file -in @('icacls','chmod')){$state.Private=$arguments[0];if($file -eq 'chmod'){$state.Private=$arguments[1]};[IO.File]::WriteAllText((Join-Path $state.Private 'sentinel'),'harmless');if($scenario -eq 'acl-failure'){$result.ExitCode=17};return $result}
        if($file -eq 'dotnet'){
            if($arguments[0] -eq 'build'){
                if($scenario -eq 'build-failure'){$result.ExitCode=17;$result.Output='protected-raw-build-sentinel';$result.Error='protected-raw-build-sentinel'}
                if($scenario -eq 'build-timeout'){throw $buildTimeout}
            }
            if($arguments[0] -eq 'run'){
                if($scenario -cin $workloadScenarios){
                    $result.ExitCode=if($scenario -cin @('workload-invalid-success','workload-recorded-failure-success')){0}else{17}
                    $result.Output="Q243 kernel entry start`nQ243 kernel K25 failed`nQ243 kernel result NotConfirmed evidence-not-confirmed 0`nprotected-config-sentinel"
                    $result.Output+="`nQ243 kernel k14 initial-create start`nQ243 kernel k14 initial-create result Refused connection-unavailable 0 false`nQ243 kernel k14 uid-delete start`nQ243 kernel k14 uid-delete exception null-reference unknown"
                    $result.Error='protected-JWT-sentinel'
                    if($scenario -like 'workload-invalid*'){$result.Output+="`nQ243 kernel result Confirmed protected-secret 999"}
                    if($scenario -ceq 'workload-oversized-failure'){$result.Output='Q243 kernel entry start'+('x'*1048576)}
                }
                if($scenario -eq 'harness-nonzero'){$result.ExitCode=17;$result.Output='protected-raw-harness-sentinel';$result.Error='protected-raw-harness-sentinel'}
                if($scenario -eq 'stock-marker'){$result.ExitCode=1;$result.Error='FAIL stock SslCaCerts accepted mismatched server name; stock path is unqualified.'}
                if($scenario -eq 'elapsed-missing'){$result.PSObject.Properties.Remove('ElapsedMilliseconds')}
                if($scenario -eq 'elapsed-malformed'){$result.ElapsedMilliseconds='protected-raw-sentinel'}
                if($scenario -eq 'elapsed-out-of-range'){$result.ElapsedMilliseconds=3600001L}
            }
            if($arguments[0] -eq 'run' -and $scenario -in @('harness-timeout','journal-invalid','journal-invalid-primary','journal-partial','journal-partial-primary','journal-partial-timeout')){
                $path=Join-Path $state.Private 'diagnostic-progress.txt'
                [IO.File]::WriteAllText($path, $(if($scenario -like 'journal-invalid*'){ "1 protected-JWT-config-sentinel 0`n" }elseif($scenario -like 'journal-partial*'){ "1 durable-entry 0`n2 catalog-init-start 10`n3 protected-fragment" }else{ "1 durable-entry 0`n2 catalog-init-start 10`n" }))
                if($scenario -in @('harness-timeout','journal-partial-timeout')){throw $syntheticTimeout}
                if($scenario -cin @('journal-invalid-primary','journal-partial-primary')){$result.ExitCode=17}
            }
            if($scenario -cnotin $workloadScenarios){$result.Output='synthetic-sdk/harness'};return $result
        }
        switch($arguments[0]){
            '--version' {$result.Output='synthetic-runtime'}
            'run' {$state.Name=$arguments[[Array]::IndexOf($arguments,'--name')+1];$state.Created=$scenario -ne 'absent-launch';if($scenario -in @('partial-launch','absent-launch')){$result.ExitCode=17}}
            'exec' {if('--request-timeout=3s' -notin $arguments){throw 'Missing kubectl timeout'};if($scenario -eq 'readiness-deadline'){if($seconds -gt 1){throw 'Unbounded command budget'};throw [TimeoutException]::new('synthetic timeout')};$result.Output='ok'}
            'cp' {
                $state.CopyCalls++
                $failAt=switch($scenario){'copy-certificate-failure'{2};'copy-key-failure'{3};default{1}}
                if($scenario -like 'copy-*' -and $state.CopyCalls -eq $failAt){
                    $result.Output='protected-raw-copy-sentinel';$result.Error='protected-raw-copy-sentinel'
                    if($scenario -ceq 'copy-managed-failure'){throw [InvalidOperationException]::new('protected-raw-copy-sentinel')}
                    if($scenario -ceq 'copy-timeout'){throw $copyTimeout}
                    $result.ExitCode=if($scenario -ceq 'copy-invalid-exit'){'protected-raw-copy-sentinel'}elseif($scenario -ceq 'copy-primary-cleanup-failure'){23}else{17}
                    break
                }
                [IO.File]::WriteAllText($arguments[-1],'harmless sentinel')
            }
            'inspect' {if($scenario -eq 'inspect-failure' -or !$state.Created){$result.ExitCode=17}else{$label=if($scenario -eq 'foreign'){'foreign'}else{$state.Name};$result.Output=ConvertTo-Json -Depth 5 -Compress @(@{Id='owned-id';Config=@{Labels=@{'haby.qualification'=$label}};Mounts=@(@{Type='volume';Name='owned-volume';Destination='/var/lib/rancher/k3s'})})}}
            'stop' {if($scenario -in @('stop-failure','copy-primary-cleanup-failure')){$result.ExitCode=17}}
            'remove' {if($scenario -eq 'remove-failure'){$result.ExitCode=17}else{if('--volumes' -notin $arguments -or $arguments[-1] -ne 'owned-id'){throw 'Unsafe removal'};$state.Removed=$true;$state.Volumes=$false}}
            'list' {if($file -eq 'docker'){throw 'docker list is not a supported alias'};if($state.Created -and !$state.Removed){$result.Output='owned-id'}}
            'ps' {if($state.Created -and !$state.Removed){$result.Output='owned-id'}}
            'volume' {if($state.Volumes){$result.Output='owned-volume'};if($arguments[-1] -eq 'name=foreign-volume'){$result.Output='foreign-volume'}}
            default {throw 'Unexpected synthetic command'}
        }
        return $result
    }.GetNewClosure()
    $evidence=Join-Path $PSScriptRoot ("../.artifacts/regression-"+[Guid]::NewGuid().ToString("N")+".txt")
    if($scenario -eq 'evidence-failure'){$evidence=((Get-Process -Id $PID).Path+[IO.Path]::DirectorySeparatorChar+[Guid]::NewGuid().ToString('N'))}
    $caught=$false
    $diagnosticMode=$scenario -in @('harness-timeout','journal-invalid','journal-invalid-primary','journal-partial','journal-partial-primary','journal-partial-timeout')
    try{& $launcher -Runtime $runtime -CommandRunner $runner -CommandTimeoutSeconds 1 -ReadinessTimeoutSeconds 1 -EvidencePath $evidence -DurableActions:$diagnosticMode -ProbeStockTls:($scenario -eq 'stock-marker') -DeploymentKernel:($scenario -cin $workloadScenarios) | Out-Null}catch{$caught=$true}
    if($scenario -cin $workloadScenarios){
        $safeLines=@(Get-Content $evidence)
        if(!$caught -or 'Harness: FAIL' -cnotin $safeLines -or 'Run-owned private directory removed.' -cnotin $safeLines -or
            @($safeLines | Where-Object {$_ -cmatch '^Run-owned container and [0-9]+ anonymous volumes absent\.$'}).Count -ne 1){throw 'Workload failure/cleanup attribution lost'}
        if($safeLines -match 'protected-|JWT|config-sentinel'){throw 'Protected workload output leaked'}
        if($scenario -cin @('workload-safe-failure','workload-secret-failure') -and
            'Diagnostic workload: kernel K25 failed' -cnotin $safeLines){throw 'Finite workload record discarded on nonzero exit'}
        if($scenario -cin @('workload-safe-failure','workload-secret-failure','workload-recorded-failure-success') -and
            ('Diagnostic workload: kernel k14 initial-create result Refused connection-unavailable 0 false' -cnotin $safeLines -or
             'Diagnostic workload: kernel k14 uid-delete exception null-reference unknown' -cnotin $safeLines)){throw 'K14 result/exception lost across nonzero or recorded-failure launcher closure'}
        if($scenario -like 'workload-invalid*' -and 'Diagnostic workload: invalid' -cnotin $safeLines){throw 'Invalid workload record accepted'}
        if($scenario -ceq 'workload-oversized-failure' -and 'Diagnostic workload: oversized' -cnotin $safeLines){throw 'Oversized workload record accepted'}
        if($scenario -cnotin @('workload-invalid-success','workload-recorded-failure-success') -and 'Failure: stage=harness; reason=nonzero; category=RuntimeException' -cnotin $safeLines){throw 'Primary workload failure replaced'}
    }
    if($caught -ne ($scenario -cnotin @('success','elapsed-missing','elapsed-malformed','elapsed-out-of-range'))){throw "Wrong exit: $scenario"}
    $buildCalls=@($state.Calls | Where-Object {$_[0] -ceq 'build'})
    $harnessCalls=@($state.Calls | Where-Object {$_[0] -ceq 'run' -and '--project' -cin $_})
    $fixtureCalls=@($state.Calls | Where-Object {$_[0] -ceq 'run' -and '--name' -cin $_})
    $buildBounds=@($state.TimedCalls | Where-Object {$_.Arguments[0] -ceq 'build'})
    $harnessBounds=@($state.TimedCalls | Where-Object {$_.Arguments[0] -ceq 'run'})
    if($scenario -ceq 'acl-failure' -and $buildCalls.Count -ne 0){throw 'Build ran after private-directory setup failure'}
    if($scenario -cne 'acl-failure' -and ($buildCalls.Count -ne 1 -or $buildCalls[0] -cnotcontains '--no-restore' -or $buildCalls[0] -cnotcontains 'Debug' -or
        $buildCalls[0] -cnotcontains 'tools/Haby.Kubernetes.Qualification' -or $buildBounds.Count -ne 1 -or $buildBounds[0].Seconds -ne 180)){throw "Explicit bounded same-output build missing: $scenario"}
    if($harnessBounds.Count -ne $harnessCalls.Count -or @($harnessBounds | Where-Object {$_.Seconds -ne 180}).Count -ne 0){throw "Harness execution bound changed: $scenario"}
    if($scenario -in @('build-failure','build-timeout')){
        $lines=@(Get-Content $evidence)
        if($harnessCalls.Count -ne 0 -or @($state.Calls | Where-Object {$_[0] -ceq 'run'}).Count -ne 0 -or
            'Stage: launch-start' -cin $lines -or 'Harness: PASS' -cin $lines -or
            'Failure: stage=build; reason='+$(if($scenario -eq 'build-failure'){'nonzero; category=RuntimeException'}else{'command; category=TimeoutException'}) -cnotin $lines -or
            $lines -match 'protected-raw-build-sentinel' -or (Test-Path -LiteralPath $state.Private)){throw "Build failure reached external fixture or leaked output: $scenario"}
        if($scenario -eq 'build-timeout' -and 'Diagnostic process: command-deadline; elapsed-ms=180001' -cnotin $lines){throw 'Build timeout attribution lost'}
        if($scenario -eq 'build-failure' -and 'Build exit code: 17' -cnotin $lines){throw 'Build nonzero exit lost'}
        if($scenario -eq 'build-timeout' -and 'Build exit code: unavailable' -cnotin $lines){throw 'Build timeout invented an exit code'}
    }elseif($scenario -eq 'stock-marker'){
        $lines=@(Get-Content $evidence)
        if($harnessCalls.Count -ne 1 -or 'FAIL stock SslCaCerts accepted mismatched server name; stock path is unqualified.' -cnotin $lines -or
            'Harness exit code: 1' -cnotin $lines -or 'Harness: FAIL' -cnotin $lines -or !$state.Removed -or $state.Volumes -or
            (Test-Path -LiteralPath $state.Private)){throw 'Stock probe marker or cleanup lost'}
    }elseif($scenario -eq 'harness-nonzero'){
        $lines=@(Get-Content $evidence)
        if($harnessCalls.Count -ne 1 -or 'Harness exit code: 17' -cnotin $lines -or
            'Harness command elapsed origin: before-process-start-through-output-drain; prebuilt-output' -cnotin $lines -or
            'Harness: FAIL' -cnotin $lines -or $lines -match 'protected-raw-harness-sentinel' -or
            !$state.Removed -or $state.Volumes -or (Test-Path -LiteralPath $state.Private)){throw 'Nonzero harness exposed output or lost cleanup'}
    }elseif($scenario -in @('success','elapsed-missing','elapsed-malformed','elapsed-out-of-range','harness-timeout')){
        if($harnessCalls.Count -ne 1 -or $harnessCalls[0] -cnotcontains '--no-build' -or $harnessCalls[0] -cnotcontains '--no-restore' -or
            $harnessCalls[0] -cnotcontains 'Debug' -or $harnessCalls[0] -cnotcontains 'tools/Haby.Kubernetes.Qualification' -or
            $fixtureCalls.Count -ne 1 -or
            [Array]::IndexOf($state.Calls,$buildCalls[0]) -ge [Array]::IndexOf($state.Calls,$fixtureCalls[0]) -or
            [Array]::IndexOf($state.Calls,$buildCalls[0]) -ge [Array]::IndexOf($state.Calls,$harnessCalls[0])){throw "Prebuilt execution ordering or bound changed: $scenario"}
    }
    if($scenario -cin @('success','elapsed-missing','elapsed-malformed','elapsed-out-of-range')){
        $lines=Get-Content $evidence
        $expectedElapsed=if($scenario -ceq 'success'){'Harness command elapsed milliseconds: 42'}else{'Harness command elapsed milliseconds: unavailable'}
        if($expectedElapsed -notin $lines -or 'Build command elapsed milliseconds: 42' -notin $lines -or
            'Build command elapsed origin: before-process-start-through-output-drain; pre-fixture' -notin $lines -or
            'Harness command elapsed origin: before-process-start-through-output-drain; prebuilt-output' -notin $lines -or
            'Build: PASS' -notin $lines -or
            'Harness: PASS' -notin $lines -or $lines -match 'protected-raw-sentinel|3600001'){throw 'Unsafe or missing successful command timing'}
    }
    if(Test-Path -LiteralPath $state.Private){throw "Private leak: $scenario"}
    if($scenario -like 'copy-*'){
        $lines=@(Get-Content $evidence)
        $ids=@('copy-server-ca','copy-admin-certificate','copy-admin-key')
        $failedIndex=switch($scenario){'copy-certificate-failure'{1};'copy-key-failure'{2};default{0}}
        $expected=@(foreach($index in 0..$failedIndex){'Copy operation: '+$ids[$index]+'; state=start';if($index -lt $failedIndex){'Copy operation: '+$ids[$index]+'; state=complete'}})
        if((@($lines | Where-Object {$_ -clike 'Copy operation:*'}) -join "`n") -cne ($expected -join "`n") -or
            ('Copy failure: operation='+$ids[$failedIndex]) -cnotin $lines -or 'Stage: harness-start' -cin $lines -or
            !$state.Removed -or $state.Volumes -or $lines -match 'protected-raw-copy-sentinel'){throw 'Unsafe or incomplete copy failure attribution'}
        $exitRecords=@($lines | Where-Object {$_ -clike 'Copy native exit:*'})
        if($scenario -cin @('copy-managed-failure','copy-invalid-exit','copy-timeout')){
            if($exitRecords.Count -ne 0){throw 'Native result invented for managed or invalid failure'}
        }elseif($exitRecords.Count -ne 1 -or $exitRecords[0] -cne $(if($scenario -ceq 'copy-primary-cleanup-failure'){'Copy native exit: 23'}else{'Copy native exit: 17'})){throw 'Primary copy exit lost or overwritten by cleanup'}
        if($scenario -ceq 'copy-managed-failure' -and 'Diagnostic process: unknown' -cnotin $lines){throw 'Managed failure relabeled as a native process outcome'}
        if($scenario -ceq 'copy-timeout' -and 'Diagnostic process: command-deadline; elapsed-ms=42' -cnotin $lines){throw 'Existing process kind lost'}
        if($scenario -ceq 'copy-primary-cleanup-failure' -and 'Container stop: FAIL' -cnotin $lines){throw 'Independent cleanup failure lost'}
    }
    if($scenario -in @('success','elapsed-missing','elapsed-malformed','elapsed-out-of-range','stop-failure','evidence-failure','partial-launch','readiness-deadline') -and (!$state.Removed -or $state.Volumes)){throw "Owned storage leaked: $scenario"}
    if($scenario -eq 'foreign' -and $state.Removed){throw 'Foreign container deleted'}
    foreach($call in $state.Calls){if('foreign-volume' -in $call -and $call[0] -in @('remove','volume')){throw 'Foreign volume targeted'}}
    if($diagnosticMode){
        $lines=Get-Content $evidence
        if($lines -match 'protected-|JWT|config-sentinel'){throw 'Protected journal content leaked'}
        if($scenario -in @('harness-timeout','journal-partial-timeout')){
            $hasPrimaryFailureRecord=-not ('Failure: stage=harness; reason=command; category=TimeoutException' -notin $lines)
            $hasProcessKindElapsedRecord=-not ('Diagnostic process: command-deadline; elapsed-ms=180001' -notin $lines)
            $hasJournalProgressRecord=-not ('Diagnostic progress: 2 catalog-init-start 10' -notin $lines)
            $hasNoHarnessPass=-not ('Harness: PASS' -in $lines)
            $hasCatalogUnknownRecord=-not ('Diagnostic inner catalog: unknown' -notin $lines)
            if(!$hasPrimaryFailureRecord -or !$hasProcessKindElapsedRecord -or !$hasJournalProgressRecord -or !$hasNoHarnessPass -or !$hasCatalogUnknownRecord){
                Write-Output ('primary-failure-record='+$hasPrimaryFailureRecord.ToString().ToLowerInvariant())
                Write-Output ('process-kind-elapsed-record='+$hasProcessKindElapsedRecord.ToString().ToLowerInvariant())
                Write-Output ('journal-progress-record='+$hasJournalProgressRecord.ToString().ToLowerInvariant())
                Write-Output ('harness-pass-absent='+$hasNoHarnessPass.ToString().ToLowerInvariant())
                Write-Output ('catalog-unknown-record='+$hasCatalogUnknownRecord.ToString().ToLowerInvariant())
                $primaryRecords=@($lines | Where-Object {$_ -cmatch '^Failure:'})
                $primaryCategory='other'
                if($primaryRecords.Count -eq 0){$primaryCategory='missing'}
                elseif($primaryRecords.Count -gt 1){$primaryCategory='ambiguous'}
                elseif($primaryRecords[0] -cmatch '^Failure: stage=(?:setup|launch|readiness|copy|harness); reason=(?:command|nonzero|deadline|child-deadline); category=([A-Za-z][A-Za-z0-9]{0,63})$'){
                    $primaryCategory=switch -CaseSensitive ($Matches[1]){
                        'TimeoutException' {'timeout'}
                        'RuntimeException' {'runtime'}
                        'MethodInvocationException' {'method-invocation'}
                        'CommandNotFoundException' {'command-not-found'}
                        'ParameterBindingException' {'parameter-binding'}
                        default {'other'}
                    }
                }
                $processRecords=@($lines | Where-Object {$_ -cmatch '^Diagnostic process:'})
                $processOutcome='invalid'
                if($processRecords.Count -eq 0){$processOutcome='missing'}
                elseif($processRecords.Count -gt 1){$processOutcome='ambiguous'}
                elseif($processRecords[0] -ceq 'Diagnostic process: unknown'){$processOutcome='unknown'}
                elseif($processRecords[0] -cmatch '^Diagnostic process: (command-deadline|output-drain-deadline|owned-termination-failure); elapsed-ms=([0-9]{1,7})$'){
                    if([long]$Matches[2] -le 3600000){$processOutcome=$Matches[1]}
                }
                Write-Output ('primary-category='+$primaryCategory)
                Write-Output ('process-outcome='+$processOutcome)
                throw 'Primary timeout/progress/unknown cleanup not retained'
            }
        }
        if($scenario -like 'journal-partial*' -and ('Diagnostic journal: partial-prefix' -cnotin $lines -or 'Diagnostic journal incomplete: final unterminated fragment discarded; FAILURE' -cnotin $lines -or 'Diagnostic progress: 2 catalog-init-start 10' -cnotin $lines -or !$caught)){throw 'Partial journal became valid/success or lost its validated prefix'}
        if($scenario -like 'journal-invalid*' -and 'Diagnostic journal: invalid' -notin $lines){throw 'Diagnostic failure missing'}
        if($scenario -cin @('journal-invalid-primary','journal-partial-primary') -and 'Failure: stage=harness; reason=nonzero; category=RuntimeException' -notin $lines){throw 'Diagnostic failure replaced primary harness failure'}
    }
    if($scenario -in @('partial-launch','acl-failure','copy-failure')){
        $idLine=Get-Content $evidence | Where-Object {$_ -like 'Evidence ID: *'}
        $rejected=$false
        try{Assert-QualificationScenario $evidence $idLine.Substring(13) 'readiness-failure' $caught $true}catch{$rejected=$true}
        if(!$rejected){throw "Earlier injected $scenario accepted as readiness failure"}
    }
    Write-Output "PASS lifecycle $runtime $scenario (synthetic)"
}
}
# Fixed K14 substep records only; neither API requests nor a cluster are involved.
$k14Records=@(
    'Q243 kernel k14 initial-create start',
    'Q243 kernel k14 initial-create result Confirmed response-confirmed 1 true',
    'Q243 kernel k14 initial-create result Refused connection-unavailable 0 false',
    'Q243 kernel k14 old-uid-observe result NotConfirmed evidence-not-confirmed 0 false',
    'Q243 kernel k14 old-uid-observe result unknown unknown unknown false',
    'Q243 kernel k14 uid-delete start',
    'Q243 kernel k14 uid-delete completed',
    'Q243 kernel k14 absence-poll start',
    'Q243 kernel k14 absence-poll result true 1',
    'Q243 kernel k14 absence-poll result false 0',
    'Q243 kernel k14 absence-poll result false 50',
    'Q243 kernel k14 absence-poll result true 53',
    'Q243 kernel k14 absence-poll result false 600',
    'Q243 kernel k14 absence-poll result false 601',
    'Q243 kernel k14 replacement-create start',
    'Q243 kernel k14 replacement-create completed',
    'Q243 kernel k14 old-uid-observe start',
    'Q243 kernel k14 assertion start',
    'Q243 kernel k14 assertion passed',
    'Q243 kernel k14 assertion failed')
foreach($phase in @('initial-create','uid-delete','absence-poll','replacement-create','old-uid-observe','assertion')){
    foreach($category in @('canceled','null-reference','contract','invalid-operation','unknown')){
        $k14Records+="Q243 kernel k14 $phase exception $category unknown"
    }
    foreach($status in @('100','200','404','409','422','599','unknown')){
        $k14Records+="Q243 kernel k14 $phase exception http $status"
    }
}
foreach($line in $k14Records){
    $parsed=Read-QualificationWorkloadEvidence kernel ("Q243 kernel entry start`n"+$line) 'protected-JWT-header-body-sentinel'
    $expected='Diagnostic workload: '+$line.Substring(5)
    if($parsed.Status -cne 'valid' -or $expected -cnotin $parsed.Records -or ($parsed.Records -match 'protected|JWT|sentinel')){throw 'Fixed K14 record missing or arbitrary error text retained'}
    $failure=$line.Contains(' exception ',[StringComparison]::Ordinal) -or $line.EndsWith(' assertion failed',[StringComparison]::Ordinal)
    if($parsed.HasFailure -ne $failure){throw 'K14 exception/assertion failure did not fail closed'}
    $wrongMode=Read-QualificationWorkloadEvidence configured ("Q243 configured entry start`n"+$line) ''
    if($wrongMode.Status -cne 'invalid' -or @($wrongMode.Records).Count){throw 'K14 record accepted in configured mode'}
}
$k14Invalid=@(
    'Q243 kernel k14 protected-JWT-name start',
    'Q243 kernel k14 unknown start',
    'Q243 kernel k14 uid-delete result Confirmed response-confirmed 1 true',
    'Q243 kernel k14 initial-create completed',
    'Q243 kernel k14 initial-create result protected response-confirmed 1 true',
    'Q243 kernel k14 initial-create result Confirmed protected 1 true',
    'Q243 kernel k14 initial-create result Confirmed response-confirmed 4 true',
    'Q243 kernel k14 initial-create result Confirmed response-confirmed 1 True',
    'Q243 kernel k14 initial-create result Confirmed response-confirmed 1 true extra',
    'Q243 kernel k14 absence-poll result true -1',
    'Q243 kernel k14 absence-poll result true 602',
    'Q243 kernel k14 absence-poll result true 00',
    'Q243 kernel k14 absence-poll result protected 1',
    'Q243 kernel k14 absence-poll completed',
    'Q243 kernel k14 assertion completed',
    'Q243 kernel k14 replacement-create receipt true',
    'Q243 kernel k14 uid-delete exception protected unknown',
    'Q243 kernel k14 uid-delete exception http 99',
    'Q243 kernel k14 uid-delete exception http 600',
    'Q243 kernel k14 uid-delete exception http protected',
    'Q243 kernel k14 uid-delete exception null-reference 404',
    ('Q243 kernel k14 uid-delete exception http 404 '+('x'*161)))
foreach($line in $k14Invalid){
    $parsed=Read-QualificationWorkloadEvidence kernel ("Q243 kernel entry start`n"+$line) ''
    if($parsed.Status -cne 'invalid' -or @($parsed.Records).Count){throw 'Hostile or unknown K14 record retained'}
}
$parsed=Read-QualificationWorkloadEvidence kernel ("Q243 kernel entry start`n"+('Q243 kernel k14 uid-delete start'+'x'*1048576)) ''
if($parsed.Status -cne 'oversized' -or @($parsed.Records).Count){throw 'K14 byte limit bypassed'}
$parsed=Read-QualificationWorkloadEvidence kernel ("Q243 kernel entry start`n"+("Q243 kernel k14 uid-delete start`n"*255)) ''
if($parsed.Status -cne 'valid' -or $parsed.Records.Count -ne 256){throw 'K14 existing record ceiling reduced'}
$parsed=Read-QualificationWorkloadEvidence kernel ("Q243 kernel entry start`n"+("Q243 kernel k14 uid-delete start`n"*256)) ''
if($parsed.Status -cne 'oversized' -or @($parsed.Records).Count){throw 'K14 existing record ceiling increased'}
foreach($line in @(
    'Q243 kernel k24 uid-delete start', 'Q243 kernel k24 uid-delete completed',
    'Q243 kernel k24 absence-poll start', 'Q243 kernel k24 absence-poll result true 53',
    'Q243 kernel k24 absence-poll result false 601', 'Q243 kernel k24 absence-poll exception canceled unknown',
    'Q243 kernel k24 absence-poll exception http 409', 'Q243 kernel k24 replacement-create start',
    'Q243 kernel k24 replacement-create completed')){
    $parsed=Read-QualificationWorkloadEvidence kernel ("Q243 kernel entry start`n"+$line) 'protected-error-sentinel'
    if($parsed.Status -cne 'valid' -or ('Diagnostic workload: '+$line.Substring(5)) -cnotin $parsed.Records){throw 'Fixed K24 replacement record missing'}
}
foreach($line in @('Q243 kernel k24 initial-create start','Q243 kernel k24 old-uid-observe start',
    'Q243 kernel k24 absence-poll result false 602','Q243 kernel k24 absence-poll result false 0601',
    'Q243 kernel k24 absence-poll result false 1000000','Q243 kernel k24 absence-poll exception protected unknown')){
    $parsed=Read-QualificationWorkloadEvidence kernel ("Q243 kernel entry start`n"+$line) ''
    if($parsed.Status -cne 'invalid' -or @($parsed.Records).Count){throw 'Unknown or unbounded K24 replacement record retained'}
}
Write-Output 'PASS kernel K24 replacement phases and finite 601-read evidence; unknown/over-bound records rejected (synthetic)'
Write-Output 'PASS fixed K14 substeps: closed results/receipt/status/count, exception failure, hostile/unknown/mode rejection and unchanged bounds (synthetic)'

# Pure parser controls for both existing workload modes, never a cluster execution.
foreach($stage in @('Execute','ExpectedLoss','Recover','ReceiptPresence','ReceiptDecode','Readback','Projection',
    'ObservationReceipt','ApiResponsePresence','ApiResponseReceipt','U06Outcome','U06Source','U06PatchCount',
    'U06SavedUid','U06TargetImage','U06PatchPresence','U06ResourceVersion','U06PatchUid','U06ResponseUid','U06ResponseGeneration')){
    foreach($suffix in @('Start','failure unknown 1')){
        $line="Q243 configured update $stage $suffix"
        $parsed=Read-QualificationWorkloadEvidence configured ("Q243 configured entry start`n"+$line) 'protected-error-sentinel'
        if($parsed.Status -cne 'valid' -or ('Diagnostic workload: '+$line.Substring(5)) -cnotin $parsed.Records -or
            $parsed.HasFailure -ne ($suffix -ceq 'failure unknown 1')){throw 'UPDATE stage/failure diagnostic missing'}
        $wrongMode=Read-QualificationWorkloadEvidence kernel ("Q243 kernel entry start`nQ243 kernel update $stage $suffix") ''
        if($wrongMode.Status -cne 'invalid' -or @($wrongMode.Records).Count){throw 'UPDATE diagnostic accepted for wrong mode'}
    }
}
foreach($suffix in @('Execute Returned','ExpectedLoss Observed','ExpectedLoss Failed','Execute Completed','Recover Completed',
    'ReceiptPresence Passed','ApiResponsePresence Passed','U06Outcome Passed','U06Outcome Failed',
    'Execute result Refused owned-state-conflict 0','Recover result Confirmed confirmed 1','Execute result unknown unknown unknown')){
    $line="Q243 configured update $suffix"
    $parsed=Read-QualificationWorkloadEvidence configured ("Q243 configured entry start`n"+$line) ''
    if($parsed.Status -cne 'valid' -or ('Diagnostic workload: '+$line.Substring(5)) -cnotin $parsed.Records -or
        $parsed.HasFailure -ne ($suffix -cin @('ExpectedLoss Failed','U06Outcome Failed'))){throw 'UPDATE result/condition diagnostic missing'}
}
foreach($suffix in @('protected-stage Start','Execute failure protected-exception 1','Execute failure unknown 4',
    'Execute failure unknown 01','Execute result Refused protected-reason 1','Execute result protected-outcome unknown 1',
    'Execute result Refused unknown 1 protected-bootstrap','Recover Observed','Readback result Confirmed confirmed 1',
    'Execute failure unknown 1 protected-response','U06Outcome Completed')){
    $parsed=Read-QualificationWorkloadEvidence configured ("Q243 configured entry start`nQ243 configured update $suffix") ''
    if($parsed.Status -cne 'invalid' -or @($parsed.Records).Count){throw 'Unrecognized UPDATE diagnostic value published'}
}
Write-Output 'PASS post-U05 UPDATE operation/condition stages, expected loss/normal return, scoped counts and hostile-value rejection (synthetic)'
foreach($stage in @('update-target-envelope','update-import','update-image-baseline','update-plan')){
    foreach($state in @('start','completed','failed')){
        $line="Q243 configured $stage $state"
        $parsed=Read-QualificationWorkloadEvidence configured ("Q243 configured entry start`n"+$line) 'protected-error-sentinel'
        if($parsed.Status -cne 'valid' -or ('Diagnostic workload: '+$line.Substring(5)) -cnotin $parsed.Records -or
            $parsed.HasFailure -ne ($state -ceq 'failed')){throw 'Fixed UPDATE setup diagnostic missing'}
        $wrongMode=Read-QualificationWorkloadEvidence kernel ("Q243 kernel entry start`nQ243 kernel $stage $state") ''
        if($wrongMode.Status -cne 'invalid' -or @($wrongMode.Records).Count){throw 'UPDATE setup stage accepted for wrong mode'}
    }
}
foreach($line in @('Q243 configured update-import protected-secret','Q243 configured update-import failed protected-secret')){
    $parsed=Read-QualificationWorkloadEvidence configured ("Q243 configured entry start`n"+$line) ''
    if($parsed.Status -cne 'invalid' -or @($parsed.Records).Count){throw 'Unrecognized UPDATE setup detail published'}
}
Write-Output 'PASS fixed UPDATE setup stages; closed value-free states, failure attribution and hostile/mode rejection (synthetic)'
foreach($mode in @('kernel','configured')){
    $case=if($mode -ceq 'kernel'){'K25'}else{'C07'}
    $valid="Q243 $mode entry start`nQ243 $mode $case failed"
    $parsed=Read-QualificationWorkloadEvidence $mode $valid 'protected-JWT-sentinel'
    if($parsed.Status -cne 'valid' -or !$parsed.HasFailure -or "Diagnostic workload: $mode $case failed" -cnotin $parsed.Records -or ($parsed.Records -match 'JWT|protected')){throw 'Closed workload parser lost mode or leaked arbitrary text'}
    if($mode -ceq 'configured'){
        $rpc=Read-QualificationWorkloadEvidence $mode ($valid+"`nQ243 configured rpc Unavailable") ''
        if($rpc.Status -cne 'valid' -or 'Diagnostic workload: configured rpc Unavailable' -cnotin $rpc.Records){throw 'Configured closed RPC status not retained'}
        $rpc=Read-QualificationWorkloadEvidence $mode ($valid+"`nQ243 configured rpc protected-secret") ''
        if($rpc.Status -cne 'invalid' -or @($rpc.Records).Count){throw 'Unrecognized RPC status published'}
    }
    foreach($invalid in @("Q243 $mode protected-secret passed","Q243 $mode result Confirmed response-confirmed 4","Q243 $mode result Invalid protected-secret 0")){
        $parsed=Read-QualificationWorkloadEvidence $mode ($valid+"`n"+$invalid) ''
        if($parsed.Status -cne 'invalid' -or @($parsed.Records).Count){throw 'Invalid workload record was published'}
    }
    $parsed=Read-QualificationWorkloadEvidence $mode (("Q243 $mode entry start`n")*257) ''
    if($parsed.Status -cne 'oversized' -or @($parsed.Records).Count){throw 'Workload record cap bypassed'}
}
Write-Output 'PASS closed workload failure channel: both modes, nonzero primary/cleanup, secret rejection and bounds (synthetic)'
if($WorkloadEvidenceOnly){return}

# Keep configured bridge/certificate controls with an isolated synthetic binding;
# the fixture copy can never become an accepted repository/public image mapping.
if($IsLinux){
    . (Join-Path $PSScriptRoot 'runtime-consumer-image.ps1')
    $inputModel=Get-HabyConsumerImageInputs (Split-Path $PSScriptRoot -Parent)
    $fixture=Join-Path (Split-Path $PSScriptRoot -Parent) ('.artifacts/haby-image-controls-'+[Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force $fixture|Out-Null
    try{
        foreach($entry in $inputModel.files.GetEnumerator()){$target=Join-Path $fixture $entry.Key;New-Item -ItemType Directory -Force ([IO.Path]::GetDirectoryName($target))|Out-Null;[IO.File]::WriteAllText($target,$entry.Value,[Text.UTF8Encoding]::new($false))}
        foreach($name in @('run-kubernetes-qualification.ps1','kubernetes-qualification-process.ps1','kubernetes-qualification-diagnostics.ps1')){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $fixture ('scripts/'+$name))}
        $binding=Join-Path $fixture ('tests/fixtures/runtime-consumer-images/'+$inputModel.key+'.json')
        New-Item -ItemType Directory -Force ([IO.Path]::GetDirectoryName($binding))|Out-Null
        [ordered]@{format=1;package=$HabyConsumerPackage;inputKey=$inputModel.key;digest=('sha256:'+('a'*64));builtSource=('b'*40);publisherSource=('c'*40);publisherRunId=1;proofArtifactSha256=('d'*64)}|ConvertTo-Json|Set-Content -LiteralPath $binding -Encoding utf8
        foreach($operation in @('none','inspect-bridge-gateway','validate-bridge-gateway','write-bridge-address','harness-failure','harness-invalid','harness-oversized','harness-recorded-failure-success')){
            $state=@{Name='';Private='';Removed=$false;Volumes=$true;NetworkInspected=$false}
            $runner={param($file,$arguments,$seconds)
                $result=[pscustomobject]@{ExitCode=0;Output='';Error='';ElapsedMilliseconds=42L}
                if($file -ceq 'chmod'){$state.Private=$arguments[1];return $result}
                if($file -ceq 'dotnet'){
                    $result.Output='synthetic-sdk/harness'
                    if($arguments[0] -ceq 'run'){
                        $result.Output=if($operation -ceq 'none'){"Q243 configured entry start`nQ243 configured completed completed"}else{"Q243 configured entry start`nQ243 configured C07 failed"}
                        $result.Error='protected-child-sentinel'
                        if($operation -ceq 'harness-failure'){$result.ExitCode=17}
                        if($operation -ceq 'harness-invalid'){$result.Output+="`nQ243 configured protected-child-sentinel passed"}
                        if($operation -ceq 'harness-oversized'){$result.Output='Q243 configured entry start'+('x'*1048576)}
                    }
                    return $result
                }
                switch($arguments[0]){
                    '--version' {$result.Output='synthetic-runtime'}
                    'run' {$state.Name=$arguments[[Array]::IndexOf($arguments,'--name')+1]}
                    'exec' {if('nodes' -cin $arguments){$result.Output='{"items":[{"status":{"conditions":[{"type":"Ready","status":"True"}]}}]}'}elseif('--request-timeout=3s' -cin $arguments){$result.Output='ok'}else{throw 'Forbidden consumer archive/import call'}}
                    'cp' {[IO.File]::WriteAllText($arguments[-1],'protected-key-sentinel')}
                    'inspect' {
                        $networkRead=!$state.NetworkInspected;$state.NetworkInspected=$true
                        if($networkRead -and $operation -ceq 'inspect-bridge-gateway'){$result.ExitCode=17;break}
                        if($networkRead -and $operation -ceq 'write-bridge-address'){New-Item -ItemType Directory (Join-Path $state.Private 'pod-host-address')|Out-Null}
                        $gateway=if($operation -ceq 'validate-bridge-gateway'){'protected-address-sentinel'}else{'192.0.2.2'}
                        $result.Output=ConvertTo-Json -Depth 8 -Compress @(@{Id='owned-id';Config=@{Labels=@{'haby.qualification'=$state.Name}};Mounts=@(@{Type='volume';Name='owned-volume'});NetworkSettings=@{Networks=@{bridge=@{Gateway=$gateway}}}})
                    }
                    'stop' {}
                    'remove' {$state.Removed=$true;$state.Volumes=$false}
                    'ps' {if(!$state.Removed){$result.Output='owned-id'}}
                    'volume' {if($state.Volumes){$result.Output='owned-volume'}}
                    default {throw 'Forbidden image build/save/publish or unexpected control command'}
                }
                return $result
            }.GetNewClosure()
            $evidence=Join-Path $fixture ($operation+'.txt');$caught=$false
            try{& (Join-Path $fixture 'scripts/run-kubernetes-qualification.ps1') -Runtime docker -ConfiguredDeployment -CommandRunner $runner -EvidencePath $evidence|Out-Null}catch{$caught=$true}
            $lines=@(Get-Content -LiteralPath $evidence)
            if($caught -ne ($operation -cne 'none') -or !$state.Removed -or $state.Volumes -or (Test-Path -LiteralPath $state.Private) -or $lines -match 'protected-.*-sentinel'){throw 'Configured bridge or cleanup control failed'}
            if($operation -cin @('inspect-bridge-gateway','validate-bridge-gateway','write-bridge-address') -and
                ('Stage: harness-start' -cin $lines -or ('Copy failure: operation='+$operation) -cnotin $lines)){throw 'Configured primary failure lost'}
            if($operation -ceq 'harness-failure' -and ('Diagnostic workload: configured C07 failed' -cnotin $lines -or
                'Harness exit code: 17' -cnotin $lines -or 'Failure: stage=harness; reason=nonzero; category=RuntimeException' -cnotin $lines)){throw 'Configured nonzero evidence or primary failure lost'}
            if($operation -ceq 'harness-recorded-failure-success' -and ('Harness exit code: 0' -cnotin $lines -or
                'Diagnostic workload: configured C07 failed' -cnotin $lines -or 'Harness: FAIL' -cnotin $lines -or
                'Failure: stage=harness; reason=diagnostic-failed; category=RuntimeException' -cnotin $lines)){throw 'Configured zero-exit failure evidence or primary failure lost'}
            if($operation -ceq 'harness-invalid' -and 'Diagnostic workload: invalid' -cnotin $lines){throw 'Configured malformed evidence accepted'}
            if($operation -ceq 'harness-oversized' -and 'Diagnostic workload: oversized' -cnotin $lines){throw 'Configured oversized evidence accepted'}
            Write-Output ('PASS configured bridge '+$operation+' (synthetic; no registry/image operation)')
        }
    }finally{Set-Location (Split-Path $PSScriptRoot -Parent);Remove-Item -LiteralPath $fixture -Recurse -Force}
}
# The real setup path uses only a fake existing fixture lifetime; no Docker/API is opened.
& dotnet run --project (Join-Path $PSScriptRoot '../tools/Haby.Kubernetes.Qualification') --no-restore -- --configured-setup-controls
if($LASTEXITCODE -ne 0){throw 'Configured setup lifetime controls failed'}
# Actual success uses the same process-start-through-output-drain clock; no timing threshold or sleep.
$success=Invoke-QualificationProcess -File (Get-Process -Id $PID).Path -Arguments @('-NoProfile','-Command',"'harmless-owned-child'")
if($success.ExitCode -ne 0 -or $success.Output.Trim() -cne 'harmless-owned-child' -or $success.ElapsedMilliseconds -isnot [long] -or
    $success.ElapsedMilliseconds -lt 0 -or $success.ElapsedMilliseconds -gt 3600000){throw 'Owned success elapsed missing or invalid'}
Write-Output 'PASS real owned child success elapsed; separate parent command clock'
# Actual child-process deadline, independent of the fake runtime.
$clock=[Diagnostics.Stopwatch]::StartNew();$timedOut=$false
try{Invoke-QualificationProcess -File (Get-Process -Id $PID).Path -Arguments @('-NoProfile','-Command','Start-Sleep -Seconds 60') -TimeoutSeconds 1 | Out-Null}catch [TimeoutException]{
    $timedOut=$true
    $diagnostic=Get-QualificationProcessDiagnostic $_.Exception
    if($diagnostic.Kind -cne 'command-deadline' -or $diagnostic.ElapsedMilliseconds -lt 1000 -or $diagnostic.ElapsedMilliseconds -gt 8000){throw 'Command deadline attribution missing'}
}
if(!$timedOut -or $clock.Elapsed.TotalSeconds -gt 8){throw 'Child deadline failed'}
Write-Output 'PASS real owned child process timeout; no global process kill'

# A pending output task exercises the real existing five-second drain bound without infrastructure.
$pending=[Threading.Tasks.TaskCompletionSource[string]]::new()
$drain=$false
try{Wait-QualificationOutput @($pending.Task,$pending.Task) ([Diagnostics.Stopwatch]::StartNew())}catch [TimeoutException]{
    $drain=(Get-QualificationProcessDiagnostic $_.Exception).Kind -ceq 'output-drain-deadline'
}
if(!$drain){throw 'Output drain deadline attribution missing'}
$termination=New-QualificationProcessFailure 'owned-termination-failure' 1234
if($termination -is [TimeoutException] -or (Get-QualificationProcessDiagnostic $termination).Kind -cne 'owned-termination-failure'){throw 'Termination failure type/attribution changed'}
$unsafe=[TimeoutException]::new('protected-raw-sentinel')
$unsafe.Data['QualificationOutcome']='protected-raw-sentinel'
$unsafe.Data['QualificationElapsedMilliseconds']='protected-raw-sentinel'
if((Get-QualificationProcessDiagnostic $unsafe).Kind -cne 'unknown'){throw 'Unrecognized exception data exposed'}
Write-Output 'PASS bounded process outcome attribution and private exception data'

# Read only fixed journal records, never arbitrary child output or paths.
. (Join-Path $PSScriptRoot 'kubernetes-qualification-diagnostics.ps1')
$journalRoot=Join-Path ([IO.Path]::GetTempPath()) ('haby179-'+[Guid]::NewGuid().ToString('N').Substring(0,12))
New-Item -ItemType Directory -Path $journalRoot | Out-Null
try{
    $journal=Join-Path $journalRoot 'diagnostic-progress.txt'
    if((Read-QualificationJournal $journalRoot).Status -cne 'missing'){throw 'Missing journal not explicit'}
    Initialize-QualificationJournal $journalRoot
    if((Read-QualificationJournal $journalRoot).Status -cne 'empty'){throw 'Empty journal not explicit'}
    $rejected=$false;try{Initialize-QualificationJournal $journalRoot}catch{$rejected=$true}
    if(!$rejected){throw 'CreateNew journal overwrote an existing file'}
    $prefix="1 durable-entry 0`n2 catalog-init-start 10`n3 catalog-init-completed 20`n4 namespace-create-start 30`n5 namespace-create-completed 40`n6 durable-suite-start 50`n"
    $pairs="7 runtime-auth-start 60`n8 runtime-auth-completed 70`n9 initial-delivery-start 80`n10 runtime-auth-start 90`n11 runtime-auth-completed 100`n"
    $valid=$prefix+$pairs+"12 mixed-delivery-start 110`n13 mixed-preparation-completed 120`n14 mixed-documents-completed 130`n15 mixed-identity-completed 140`n16 mixed-publication-completed 150`n17 mixed-delivery-completed 160`n18 namespace-cleanup-start 170`n19 namespace-absence-confirmed 180`n20 catalog-dispose-start 190`n21 catalog-dispose-completed 200`n"
    [IO.File]::WriteAllText($journal,$valid)
    $parsed=Read-QualificationJournal $journalRoot
    if($parsed.Status -cne 'valid' -or $parsed.Records.Count -ne 21){throw 'Valid journal rejected'}
    $published=@(Format-QualificationJournal $parsed)
    if($published -match '^PASS |^Harness: PASS$'){throw 'Progress became a passing gate row'}
    if('Diagnostic managed elapsed origin: journal-initialization; excludes-build-startup; clock=monotonic' -notin $published -or
        'Diagnostic inner namespace: confirmed' -notin $published -or 'Diagnostic inner catalog: confirmed' -notin $published){throw 'Managed clock origin or actual cleanup evidence missing'}
    $entryIds=@('durable-entry','catalog-init-start','catalog-init-completed','namespace-create-start','namespace-create-completed','durable-suite-start','runtime-auth-start','runtime-auth-completed')
    $finalIds=@('runtime-final-revoke-start','runtime-final-revoke-completed','runtime-final-history-start','runtime-final-history-completed','runtime-final-authorization-start','runtime-final-authorization-completed')
    $disposeIds=@('runtime-candidate-dispose-start','runtime-candidate-dispose-completed')
    $firstIds=$entryIds+$finalIds+$disposeIds
    $secondIds=$firstIds+@('initial-delivery-start','runtime-auth-start','runtime-auth-completed')+$finalIds+$disposeIds
    $cleanupIds=@('inner-cleanup-start','namespace-cleanup-start','namespace-absence-confirmed','catalog-dispose-start','catalog-dispose-completed','inner-cleanup-completed')
    $fullIds=$secondIds+@('mixed-delivery-start','mixed-preparation-completed','mixed-documents-completed','mixed-identity-completed','mixed-publication-completed','mixed-delivery-completed')+$cleanupIds
    if($fullIds.Count -ne 39){throw 'Closed boundary control inventory changed'}
    # Optional finite scenario-group boundaries preserve old journals and interrupted cleanup.
    $earlyIds=@('workload-actions-start','accepted-setup-start','accepted-setup-completed','restore-start','restore-completed',
        'accepted-setup-start','accepted-setup-completed','workload-actions-completed','identity-actions-start',
        'runtime-setup-start','runtime-setup-completed','restore-start','restore-completed','identity-actions-completed')
    $earlyPrefix=$entryIds[0..5]+$earlyIds
    $earlyFull=$earlyPrefix+$secondIds[6..($secondIds.Count-1)]+@('mixed-delivery-start','mixed-baseline-prepared',
        'mixed-preparation-completed','mixed-documents-completed','mixed-identity-completed','mixed-publication-completed','mixed-delivery-completed')+$cleanupIds
    foreach($case in @(
        @{Ids=$earlyFull;Status='valid'},
        @{Ids=($entryIds[0..5]+$earlyIds[0..1]);Status='valid'},
        @{Ids=($entryIds[0..5]+$earlyIds[0..1]+$cleanupIds);Status='valid'},
        @{Ids=($entryIds[0..5]+@('accepted-setup-completed'));Status='invalid'},
        @{Ids=($entryIds[0..5]+@('workload-actions-start','accepted-setup-start','restore-start'));Status='invalid'},
        @{Ids=($entryIds[0..5]+@('workload-actions-start','accepted-setup-start','workload-actions-completed'));Status='invalid'},
        @{Ids=($entryIds[0..5]+@('workload-actions-start','accepted-setup-start','accepted-setup-completed','accepted-setup-completed'));Status='invalid'},
        @{Ids=($entryIds[0..5]+@('restore-start'));Status='invalid'},
        @{Ids=($entryIds[0..5]+@('workload-actions-start')+(@('accepted-setup-start','accepted-setup-completed')*17));Status='invalid'},
        @{Ids=($secondIds+@('mixed-delivery-start','mixed-preparation-completed','mixed-baseline-prepared'));Status='invalid'},
        @{Ids=($entryIds[0..5]+@('workload-actions-start','protected-raw-sentinel'));Status='invalid'}
    )){
        $records=@(for($index=0;$index -lt $case.Ids.Count;$index++){($index+1).ToString()+' '+$case.Ids[$index]+' '+($index*10).ToString()})
        [IO.File]::WriteAllText($journal,($records -join "`n")+"`n")
        $parsed=Read-QualificationJournal $journalRoot
        $output=@(Format-QualificationJournal $parsed)
        if($parsed.Status -cne $case.Status -or $output -match '^PASS |^Harness: PASS$|protected-'){
            throw 'Early scenario boundaries, finite repeats or value exclusion changed'
        }
    }
    Write-Output 'PASS early scenario preparation, restore, action-group and mixed-baseline boundaries (synthetic)'

    $deliveryIds=@('delivery-provenance-start','delivery-provenance-completed','delivery-pod-start','delivery-pod-completed','delivery-token-start','delivery-token-completed',
        'delivery-host-first-start','delivery-host-first-built','delivery-host-first-credential-ready','delivery-host-first-started','delivery-first-request-start','delivery-first-request-completed',
        'delivery-host-first-dispose-start','delivery-host-first-dispose-completed','delivery-host-restart-start','delivery-host-restart-built','delivery-host-restart-credential-ready','delivery-host-restart-started',
        'delivery-first-request-start','delivery-first-request-completed','delivery-host-restart-dispose-start','delivery-host-restart-dispose-completed',
        'delivery-pod-cleanup-start','delivery-pod-cleanup-completed')
    $initialDeliveryIds=$firstIds+@('initial-delivery-start')
    $twoDeliveries=$initialDeliveryIds+$deliveryIds+@('runtime-auth-start','runtime-auth-completed')+$finalIds+$disposeIds+
        @('mixed-delivery-start','mixed-preparation-completed','mixed-documents-completed','mixed-identity-completed','mixed-publication-completed')+$deliveryIds+@('mixed-delivery-completed')+$cleanupIds
    foreach($case in @(
        @{Ids=$twoDeliveries;Status='valid'},
        @{Ids=($initialDeliveryIds+$deliveryIds[0..8]);Status='valid'},
        @{Ids=($initialDeliveryIds+@('delivery-pod-start'));Status='invalid'},
        @{Ids=($initialDeliveryIds+$deliveryIds[0..7]+@('delivery-host-first-start'));Status='invalid'},
        @{Ids=($initialDeliveryIds+$deliveryIds[0..6]+@('delivery-host-first-credential-ready'));Status='invalid'},
        @{Ids=($initialDeliveryIds+$deliveryIds[0..7]+@('delivery-host-first-started'));Status='invalid'},
        @{Ids=($initialDeliveryIds+$deliveryIds[0..6]+@('delivery-host-restart-built'));Status='invalid'},
        @{Ids=($initialDeliveryIds+$deliveryIds+@('delivery-first-request-start'));Status='invalid'},
        @{Ids=($initialDeliveryIds+@('delivery-private-payload'));Status='invalid'}
    )){
        $records=@(for($index=0;$index -lt $case.Ids.Count;$index++){($index+1).ToString()+' '+$case.Ids[$index]+' '+($index*10).ToString()})
        [IO.File]::WriteAllText($journal,($records -join "`n")+"`n")
        if((Read-QualificationJournal $journalRoot).Status -cne $case.Status){throw 'Finite delivery provenance, Pod/token, Host/request/disposal sequence changed'}
    }
    Write-Output 'PASS finite delivery boundaries distinguish outer scenarios and first/restarted Hosts without payloads (synthetic)'


    foreach($case in @(
        @{Ids=$fullIds;Status='valid';Outcome='completed'},
        @{Ids=($entryIds+@('runtime-final-revoke-start')+$disposeIds+$cleanupIds);Status='valid';Outcome='completed'},
        @{Ids=($entryIds[0..6]+$disposeIds+$cleanupIds);Status='valid';Outcome='completed'},
        @{Ids=($entryIds+@('runtime-candidate-dispose-start','runtime-candidate-dispose-failed','inner-cleanup-start','namespace-cleanup-start','namespace-cleanup-failed','catalog-dispose-start','catalog-dispose-failed','inner-cleanup-failed'));Status='valid';Outcome='failed'},
        @{Ids=($entryIds+@('runtime-final-history-start'));Status='invalid';Outcome='unknown'},
        @{Ids=($firstIds+@('runtime-candidate-dispose-completed'));Status='invalid';Outcome='unknown'},
        @{Ids=($secondIds+$disposeIds);Status='invalid';Outcome='unknown'},
        @{Ids=($secondIds+@('inner-cleanup-start','inner-cleanup-completed'));Status='invalid';Outcome='unknown'},
        @{Ids=($secondIds+$cleanupIds+@('runtime-auth-start'));Status='invalid';Outcome='unknown'},
        @{Ids=($entryIds+@('runtime-candidate-dispose-completed'));Status='invalid';Outcome='unknown'},
        @{Ids=($entryIds+@('runtime-candidate-dispose-start','inner-cleanup-start'));Status='invalid';Outcome='unknown'},
        @{Ids=($secondIds+@('inner-cleanup-start','namespace-cleanup-start','namespace-cleanup-failed','namespace-absence-confirmed'));Status='invalid';Outcome='unknown'},
        @{Ids=($secondIds+@('inner-cleanup-start','namespace-cleanup-start','namespace-absence-confirmed','catalog-dispose-start','catalog-dispose-failed','catalog-dispose-completed'));Status='invalid';Outcome='unknown'}
    )){
        $records=@(for($index=0;$index -lt $case.Ids.Count;$index++){($index+1).ToString()+' '+$case.Ids[$index]+' '+($index*10).ToString()})
        [IO.File]::WriteAllText($journal,($records -join "`n")+"`n")
        $parsed=Read-QualificationJournal $journalRoot
        $output=@(Format-QualificationJournal $parsed)
        if($parsed.Status -cne $case.Status -or ('Diagnostic inner cleanup outcome: '+$case.Outcome) -cnotin $output -or $output -match '^PASS |^Harness: PASS$|protected-'){
            throw 'Closed final-boundary ordering, terminal outcome or partial cleanup control failed'
        }
    }
    Write-Output 'PASS final durable boundaries, partial cancellation, disposal and cleanup outcome controls (synthetic)'
    # Incomplete auth pairs are valid progress, including failure paths that reach owned cleanup.
    foreach($text in @(
        "1 durable-entry 3600000`n2 catalog-init-start 3600000`n",
        ($prefix+"7 runtime-auth-start 60`n"),
        ($prefix+"7 runtime-auth-start 60`n8 runtime-auth-completed 60`n"),
        ($prefix+"7 runtime-auth-start 60`n8 runtime-auth-completed 70`n9 initial-delivery-start 80`n10 runtime-auth-start 90`n"),
        ($prefix+"7 runtime-auth-start 60`n8 namespace-cleanup-start 70`n9 namespace-absence-confirmed 80`n10 catalog-dispose-start 90`n11 catalog-dispose-completed 100`n")
    )){
        [IO.File]::WriteAllText($journal,$text)
        $partial=Read-QualificationJournal $journalRoot
        if($partial.Status -cne 'valid'){throw 'Valid equal/bounded timing or incomplete pair rejected'}
        $output=@(Format-QualificationJournal $partial)
        if($output -match '^PASS |^Harness: PASS$'){throw 'Incomplete phase became case success'}
        if($text -notmatch 'catalog-dispose-completed' -and ('Diagnostic inner namespace: unknown' -notin $output -or 'Diagnostic inner catalog: unknown' -notin $output)){throw 'Incomplete phase claimed cleanup'}
    }
    foreach($case in @(
        @{Text="1 durable-entry 0`n2 catalog-init-start 10";Status='partial-prefix'},
        @{Text="1 durable-entry 0`n3 catalog-init-start 10`n";Status='invalid'},
        @{Text="1 durable-entry 0`n2 catalog-dispose-completed 10`n";Status='invalid'},
        @{Text="1 durable-entry 0`n2 catalog-init-start 10`n3 catalog-init-completed 20`n4 namespace-create-start 30`n5 namespace-create-completed 40`n6 namespace-cleanup-start 50`n7 catalog-dispose-start 60`n8 namespace-absence-confirmed 70`n";Status='invalid'},
        @{Text=($prefix+$pairs+"12 mixed-delivery-start 110`n13 mixed-documents-completed 120`n");Status='invalid'},
        @{Text=($prefix+$pairs+"12 mixed-delivery-start 110`n13 namespace-cleanup-start 120`n14 mixed-preparation-completed 130`n");Status='invalid'},
        @{Text="1 durable-entry 0`n2 protected-JWT-SQL-config-argument-sentinel 10`n";Status='invalid'},
        @{Text="1 durable-entry 00`n";Status='invalid'},
        @{Text="1 durable-entry -1`n";Status='invalid'},
        @{Text="1 durable-entry +1`n";Status='invalid'},
        @{Text="1 durable-entry 1.0`n";Status='invalid'},
        @{Text="1 durable-entry 1e2`n";Status='invalid'},
        @{Text="1 durable-entry 3600001`n";Status='invalid'},
        @{Text="1 durable-entry 99999999999999999999`n";Status='invalid'},
        @{Text="1 durable-entry 0`n2 catalog-init-start 0 extra`n";Status='invalid'},
        @{Text=($prefix+"7 runtime-auth-start 49`n");Status='invalid'},
        @{Text=($prefix+"7 runtime-auth-completed 60`n");Status='invalid'},
        @{Text=($prefix+"7 runtime-auth-start 60`n8 runtime-auth-start 70`n");Status='invalid'},
        @{Text=($prefix+"7 runtime-auth-start 60`n8 runtime-auth-completed 70`n9 runtime-auth-completed 80`n");Status='invalid'},
        @{Text=($prefix+"7 runtime-auth-start 60`n8 runtime-auth-completed 70`n9 runtime-auth-start 80`n");Status='invalid'},
        @{Text=($prefix+$pairs+"12 runtime-auth-start 110`n");Status='invalid'},
        @{Text=($prefix+"7 initial-delivery-start 60`n");Status='invalid'},
        @{Text=($prefix+"7 mixed-delivery-start 60`n");Status='invalid'},
        @{Text=($prefix+"7 runtime-auth-start 60`n8 namespace-cleanup-start 70`n9 runtime-auth-completed 80`n");Status='invalid'},
        @{Text=('x'*16385);Status='overflow'},
        @{Text=("1 durable-entry 0`n"*257);Status='overflow'}
    )){
        [IO.File]::WriteAllText($journal,$case.Text)
        $parsed=Read-QualificationJournal $journalRoot
        $output=@(Format-QualificationJournal $parsed) -join "`n"
        if($parsed.Status -cne $case.Status -or $output -match 'protected-|JWT|SQL|config|argument|PASS'){throw 'Unsafe/incorrect rejected journal evidence'}
        if($output -notmatch 'namespace: unknown' -or $output -notmatch 'catalog: unknown'){throw 'Invalid journal claimed inner cleanup'}
    }
    Test-QualificationCallerJournalControls $journalRoot
    Remove-Item -LiteralPath $journal
    $outside=Join-Path $journalRoot 'outside.txt'
    [IO.File]::WriteAllText($outside,'protected-link-sentinel')
    if($IsWindows){New-Item -ItemType Junction -Path $journal -Target $journalRoot | Out-Null}
    else{[IO.File]::CreateSymbolicLink($journal,$outside) | Out-Null}
    if((Read-QualificationJournal $journalRoot).Status -cne 'path-refused'){throw 'Linked journal accepted'}
    $rejected=$false;try{Initialize-QualificationJournal $journalRoot}catch{$rejected=$true}
    if(!$rejected){throw 'Existing linked journal opened for writing'}
    if((Read-QualificationJournal (Join-Path $journalRoot '../escape')).Status -cne 'path-refused'){throw 'Escaped directory accepted'}
}finally{
    # Only this independently created exact directory is removed.
    if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($journalRoot)) -ne [IO.Path]::GetTempPath().TrimEnd([IO.Path]::DirectorySeparatorChar)){throw 'Test cleanup ownership mismatch'}
    if(Test-Path -LiteralPath $journal){Remove-Item -LiteralPath $journal -Force}
    Remove-Item -LiteralPath $journalRoot -Recurse -Force
}
Write-Output 'PASS bounded journal content, ordering, path refusal and no false gate success'

# Linux PATH may expose the same executable through both /usr/bin and /bin.
& {
    function Get-Command { param($Name,$CommandType,$ErrorAction) @([pscustomobject]@{Source='first-path'},[pscustomobject]@{Source='second-path'}) }
    if((Resolve-QualificationExecutable 'fixture') -ne 'first-path'){throw 'Executable resolution joined multiple paths'}
}
Write-Output 'PASS executable resolution selects one application path'
