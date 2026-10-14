# High-risk PII: uploader acknowledgment + mandatory admin release

## Behavior
1. Presidio scans a completed staged upload before publishing, notification, share-link activation, download, or external API exposure.
2. When high-risk PII is identified, show the flagged category, bounded context/redacted snippet (least disclosure), and explanation to the authenticated uploader. Do not echo full sensitive values in email or ordinary logs.
3. Require explicit unchecked-by-default checkbox: "I acknowledge that this file was flagged as potentially containing high-risk personally identifiable information. I believe this upload is appropriate for the intended recipients, and I understand it will be held for administrator review before it can be shared."
4. Until checked, submission blocked. On acknowledgment, persist uploader identity, UTC timestamp, upload ID, risk/category/finding reference, acknowledgment policy version, client IP as appropriate and immutable audit event. Send administrative review notification to configured GIS/admin addresses without PII data.
5. Set status **PendingPIIReview** (not Approved/Published); UI says "Submitted for review—recipients cannot access this file until a site administrator approves it."
6. Securely store payload in dedicated nonpublic quarantine storage (at-rest protection, malware screening, limited administrative access); no transfer/download token becomes active, no recipient notification, no background distribution, no public listing, no integration webhook containing downloadable links while pending.
7. Admin review queue with summary, risk classification, file metadata, and privileged inspection after reauthentication/role check where appropriate. Admin may approve (explicit confirmation with reason), reject (reason), or request resubmission; uploader cannot approve own submission, including users who hold an admin role. Prefer second-person review.
8. Approval rechecks malware/PII policy, expiration, permissions and recipient validity; in a **single database transaction** record decision and make file eligible for secure release. Emit recipient notifications only after commit; idempotent retry prevents duplicate notification/share. Rejected records remain inaccessible and are retained/deleted by retention policy.
9. Audit every transition with actor/time/reason, policy/model version and fingerprint; preserve records for audit without logging PII snippets. Protect admins from exposing PII in email.
10. Prevent race/bypass: authorize at every download and link endpoint using current status; disable cached tokens, pre-signed URL generation, scheduled jobs, batch APIs, thumbnails, preview URLs and connectors for PendingPIIReview. Acknowledgment never overrides mandatory approval.

## State machine
Scanning -> FlaggedHighRisk -> PendingPIIReview -> ApprovedForRelease -> Published
PendingPIIReview -> Rejected
PendingPIIReview -> NeedsResubmission
ScanError -> Quarantined (fail closed)
Do not reuse the legacy "override" flag to publish.

## Suggested schema (adapt to actual Phase 72+ database)
PIIReviewRequests(id, transfer_id, uploader_object_id, status, created_utc, policy_version, findings_reference, row_version)
PIIAcknowledgments(request_id, actor_object_id, acknowledged_utc, text_version, client_ip)
PIIReviewDecisions(request_id, reviewer_object_id, decision, reason, decision_utc, row_version)
Add status and supporting indexes to actual transfer workflow after inspecting deployed schema; never migrate production as part of TestDrop setup.
Enforce reviewer != uploader at application/service and database transition layers.

## Acceptance tests
- High risk + box unchecked -> cannot submit.
- High risk + box checked -> pending, no accessible recipient link or email.
- All download paths return denied while pending, even if previous token exists.
- Admin review role required and uploader cannot self-approve.
- Approve -> published exactly once; email one time; audit includes actor/reason.
- Reject -> no access; uploader sees rejected.
- Concurrent approval, missing malware result, failure to send email, expiration, or scan errors -> safe behavior.
- Low-risk/no-risk still follows established policy.
- Test only with synthetic PII; production untouched.

## Deployment
Implement on feature/testdrop-isolated-staging against reconciled Phase 72+ sources. Current public GitHub main is older and does not establish current production implementation. No production migration or rollout is authorized.
