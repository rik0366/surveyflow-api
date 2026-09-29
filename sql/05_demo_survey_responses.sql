-- =============================================================================
-- 20 demo SurveyResponses against the Customer Experience demo survey
-- (sql/04_demo_satisfaction_survey.sql). Designed to give the Results page
-- something to render — varied sentiment, plus richly themed Q10 long_text
-- so Text Analytics can pull out distinct themes (performance, support,
-- pricing, mobile, docs, accessibility, etc.).
--
-- Idempotent: deletes any prior responses for this survey before inserting.
-- =============================================================================

USE SurveyFlow;
GO

DECLARE @SurveyId CHAR(36) = 'd5f4e7b3-8c2a-4e9d-b1f6-7a3c5e2d4f8a';
DECLARE @Now DATETIME2(3) = SYSUTCDATETIME();

-- Reset prior demo responses
DELETE FROM dbo.SurveyResponses WHERE survey_id = @SurveyId;

-- Helper: generate a started/completed pair offset N hours into the past
-- (response took ~3 minutes).
DECLARE @s DATETIME2(3), @c DATETIME2(3);

------------------------------------------------------------------------------
-- 1 — Sarah, enthusiastic power user
------------------------------------------------------------------------------
SET @s = DATEADD(MINUTE, -3, DATEADD(HOUR, -2,   @Now)); SET @c = DATEADD(HOUR, -2,   @Now);
INSERT INTO dbo.SurveyResponses (id, survey_id, status, answers_json, current_question_index, device_type, started_at, completed_at, created_date, updated_date)
VALUES ('a0000000-0000-0000-0000-000000000001', @SurveyId, 'completed',
N'{"q_name":"Sarah","q_nps":10,"q_satisfaction":5,"q_frequency":"Daily","q_features":["Dashboard","Detailed reports","Email notifications"],"q_dimensions":{"Ease of use":5,"Performance and speed":5,"Customer support":5,"Documentation and help":5,"Value for money":5},"q_priorities":["Better performance","More features","Better support","Improved UI","Lower price"],"q_budget":{"New features":25,"Performance improvements":30,"Better support":20,"Lower prices":10,"UI/UX polish":15},"q_emoji":"😍","q_feedback":"I absolutely love this product! The interface is intuitive, the dashboard gives me exactly the data I need at a glance, and the daily reports save me hours every week. I''ve been using it for about six months and recommended it to three colleagues. The only minor wish I have is for more customization in the report templates, but honestly that''s a small thing. Keep up the great work!"}',
10, 'desktop', @s, @c, @s, @c);

------------------------------------------------------------------------------
-- 2 — Mike, mobile complaints
------------------------------------------------------------------------------
SET @s = DATEADD(MINUTE, -4, DATEADD(HOUR, -8,   @Now)); SET @c = DATEADD(HOUR, -8,   @Now);
INSERT INTO dbo.SurveyResponses (id, survey_id, status, answers_json, current_question_index, device_type, started_at, completed_at, created_date, updated_date)
VALUES ('a0000000-0000-0000-0000-000000000002', @SurveyId, 'completed',
N'{"q_name":"Mike","q_nps":8,"q_satisfaction":4,"q_frequency":"Daily","q_features":["Dashboard","API access","Mobile app"],"q_dimensions":{"Ease of use":4,"Performance and speed":4,"Customer support":5,"Documentation and help":4,"Value for money":4},"q_priorities":["More features","Better performance","Improved UI","Better support","Lower price"],"q_budget":{"New features":35,"Performance improvements":20,"Better support":15,"Lower prices":5,"UI/UX polish":25},"q_emoji":"😊","q_feedback":"Generally very happy with the platform. The web interface is solid and the API has been a game-changer for our automation pipeline. My main gripe is the mobile app — it feels like an afterthought. Half the features I use on web are missing on mobile, and the navigation is clunky. If you could bring the mobile experience up to parity with web, this would be a 10/10."}',
10, 'desktop', @s, @c, @s, @c);

------------------------------------------------------------------------------
-- 3 — Jennifer, performance frustration / churn risk
------------------------------------------------------------------------------
SET @s = DATEADD(MINUTE, -5, DATEADD(HOUR, -14,  @Now)); SET @c = DATEADD(HOUR, -14,  @Now);
INSERT INTO dbo.SurveyResponses (id, survey_id, status, answers_json, current_question_index, device_type, started_at, completed_at, created_date, updated_date)
VALUES ('a0000000-0000-0000-0000-000000000003', @SurveyId, 'completed',
N'{"q_name":"Jennifer","q_nps":4,"q_satisfaction":2,"q_frequency":"Weekly","q_features":["Dashboard","Detailed reports"],"q_dimensions":{"Ease of use":3,"Performance and speed":1,"Customer support":4,"Documentation and help":3,"Value for money":3},"q_priorities":["Better performance","Better support","Improved UI","More features","Lower price"],"q_budget":{"New features":5,"Performance improvements":50,"Better support":15,"Lower prices":5,"UI/UX polish":25},"q_emoji":"😕","q_feedback":"I want to love this product but the performance issues are really getting in the way. The dashboard takes 15-20 seconds to load on busy days, exports often fail and have to be restarted, and I''ve had to refresh the page multiple times when reports just spin forever. The support team has been responsive but they keep telling me ''we''re working on it'' without any actual fixes shipping. I''m starting to look at competitors honestly."}',
10, 'desktop', @s, @c, @s, @c);

------------------------------------------------------------------------------
-- 4 — David, price sensitive
------------------------------------------------------------------------------
SET @s = DATEADD(MINUTE, -3, DATEADD(HOUR, -20,  @Now)); SET @c = DATEADD(HOUR, -20,  @Now);
INSERT INTO dbo.SurveyResponses (id, survey_id, status, answers_json, current_question_index, device_type, started_at, completed_at, created_date, updated_date)
VALUES ('a0000000-0000-0000-0000-000000000004', @SurveyId, 'completed',
N'{"q_name":"David","q_nps":6,"q_satisfaction":3,"q_frequency":"Monthly","q_features":["Dashboard","Email notifications"],"q_dimensions":{"Ease of use":3,"Performance and speed":3,"Customer support":3,"Documentation and help":3,"Value for money":2},"q_priorities":["Lower price","Better support","More features","Improved UI","Better performance"],"q_budget":{"New features":20,"Performance improvements":10,"Better support":15,"Lower prices":40,"UI/UX polish":15},"q_emoji":"😐","q_feedback":"The product works fine for what we use it for, but the price keeps creeping up every renewal and I don''t see proportional value increases. We use maybe 30% of the features but pay the full price. A tiered pricing model based on actual usage would be much fairer. The tool itself is okay — not amazing, not bad. It does the job."}',
10, 'desktop', @s, @c, @s, @c);

------------------------------------------------------------------------------
-- 5 — Lisa, support praise
------------------------------------------------------------------------------
SET @s = DATEADD(MINUTE, -3, DATEADD(HOUR, -28,  @Now)); SET @c = DATEADD(HOUR, -28,  @Now);
INSERT INTO dbo.SurveyResponses (id, survey_id, status, answers_json, current_question_index, device_type, started_at, completed_at, created_date, updated_date)
VALUES ('a0000000-0000-0000-0000-000000000005', @SurveyId, 'completed',
N'{"q_name":"Lisa","q_nps":9,"q_satisfaction":5,"q_frequency":"Several times a week","q_features":["Dashboard","Detailed reports","Custom workflows","Email notifications"],"q_dimensions":{"Ease of use":4,"Performance and speed":4,"Customer support":5,"Documentation and help":4,"Value for money":4},"q_priorities":["Better support","More features","Better performance","Improved UI","Lower price"],"q_budget":{"New features":25,"Performance improvements":20,"Better support":30,"Lower prices":10,"UI/UX polish":15},"q_emoji":"😊","q_feedback":"The customer support team is honestly what keeps me here. Whenever I''ve had questions or run into problems, the response time has been incredible and the people are knowledgeable. Special shout-out to whoever handles the chat support — they helped me set up a complex workflow in under 30 minutes. The product itself is good, but the support is what makes it great."}',
10, 'desktop', @s, @c, @s, @c);

------------------------------------------------------------------------------
-- 6 — Tom, missing features and docs
------------------------------------------------------------------------------
SET @s = DATEADD(MINUTE, -4, DATEADD(HOUR, -36,  @Now)); SET @c = DATEADD(HOUR, -36,  @Now);
INSERT INTO dbo.SurveyResponses (id, survey_id, status, answers_json, current_question_index, device_type, started_at, completed_at, created_date, updated_date)
VALUES ('a0000000-0000-0000-0000-000000000006', @SurveyId, 'completed',
N'{"q_name":"Tom","q_nps":5,"q_satisfaction":3,"q_frequency":"Weekly","q_features":["Dashboard","Detailed reports","API access"],"q_dimensions":{"Ease of use":3,"Performance and speed":4,"Customer support":3,"Documentation and help":2,"Value for money":3},"q_priorities":["More features","Improved UI","Better performance","Better support","Lower price"],"q_budget":{"New features":50,"Performance improvements":5,"Better support":5,"Lower prices":15,"UI/UX polish":25},"q_emoji":"😐","q_feedback":"I keep hitting feature gaps. Things like bulk editing, scheduled exports to S3, conditional alerts based on multiple metrics — these all feel like obvious extensions that should exist. The roadmap page hasn''t been updated in 8 months which makes me wonder if anything is being actively developed. Documentation is also pretty thin for the API — most endpoints just have a one-line description and an example."}',
10, 'desktop', @s, @c, @s, @c);

------------------------------------------------------------------------------
-- 7 — Amanda, new user honeymoon
------------------------------------------------------------------------------
SET @s = DATEADD(MINUTE, -3, DATEADD(HOUR, -44,  @Now)); SET @c = DATEADD(HOUR, -44,  @Now);
INSERT INTO dbo.SurveyResponses (id, survey_id, status, answers_json, current_question_index, device_type, started_at, completed_at, created_date, updated_date)
VALUES ('a0000000-0000-0000-0000-000000000007', @SurveyId, 'completed',
N'{"q_name":"Amanda","q_nps":10,"q_satisfaction":5,"q_frequency":"Daily","q_features":["Dashboard","Detailed reports","Mobile app","Email notifications"],"q_dimensions":{"Ease of use":5,"Performance and speed":4,"Customer support":5,"Documentation and help":5,"Value for money":5},"q_priorities":["More features","Better support","Improved UI","Better performance","Lower price"],"q_budget":{"New features":30,"Performance improvements":15,"Better support":20,"Lower prices":10,"UI/UX polish":25},"q_emoji":"😍","q_feedback":"Just finished my first month and I''m already a huge fan. The onboarding was smooth, the documentation actually got me up and running fast, and the dashboard immediately showed me insights I didn''t know we needed. Coming from a clunky legacy system, this feels like a breath of fresh air. Already convinced my CFO to upgrade to the higher tier."}',
10, 'mobile', @s, @c, @s, @c);

------------------------------------------------------------------------------
-- 8 — James, custom workflows / learning curve
------------------------------------------------------------------------------
SET @s = DATEADD(MINUTE, -3, DATEADD(HOUR, -52,  @Now)); SET @c = DATEADD(HOUR, -52,  @Now);
INSERT INTO dbo.SurveyResponses (id, survey_id, status, answers_json, current_question_index, device_type, started_at, completed_at, created_date, updated_date)
VALUES ('a0000000-0000-0000-0000-000000000008', @SurveyId, 'completed',
N'{"q_name":"James","q_nps":7,"q_satisfaction":4,"q_frequency":"Daily","q_features":["Dashboard","Detailed reports","API access","Custom workflows"],"q_dimensions":{"Ease of use":4,"Performance and speed":3,"Customer support":4,"Documentation and help":3,"Value for money":4},"q_priorities":["Better performance","More features","Better support","Improved UI","Lower price"],"q_budget":{"New features":25,"Performance improvements":30,"Better support":20,"Lower prices":10,"UI/UX polish":15},"q_emoji":"😊","q_feedback":"Solid product overall. The custom workflows feature is incredibly powerful once you figure it out — but that''s the catch. The learning curve is steep and the examples in the docs barely scratch the surface. I''d pay for a half-day training session if you offered one. Performance is mostly fine but the workflow engine slows down noticeably when you have more than 50 active rules."}',
10, 'desktop', @s, @c, @s, @c);

------------------------------------------------------------------------------
-- 9 — Rachel, documentation pain
------------------------------------------------------------------------------
SET @s = DATEADD(MINUTE, -4, DATEADD(HOUR, -60,  @Now)); SET @c = DATEADD(HOUR, -60,  @Now);
INSERT INTO dbo.SurveyResponses (id, survey_id, status, answers_json, current_question_index, device_type, started_at, completed_at, created_date, updated_date)
VALUES ('a0000000-0000-0000-0000-000000000009', @SurveyId, 'completed',
N'{"q_name":"Rachel","q_nps":6,"q_satisfaction":3,"q_frequency":"Several times a week","q_features":["Dashboard","Detailed reports","API access"],"q_dimensions":{"Ease of use":3,"Performance and speed":4,"Customer support":4,"Documentation and help":1,"Value for money":3},"q_priorities":["Better support","Improved UI","More features","Better performance","Lower price"],"q_budget":{"New features":20,"Performance improvements":10,"Better support":20,"Lower prices":10,"UI/UX polish":40},"q_emoji":"😐","q_feedback":"The documentation is by far the weakest part of the product. So many features have undocumented behaviors that I''ve only figured out through trial and error or by asking support. The API reference is missing parameter descriptions, the user guide videos are from 2022 and don''t match the current UI, and the search on the help center returns mostly irrelevant results. Please invest in technical writers."}',
10, 'desktop', @s, @c, @s, @c);

------------------------------------------------------------------------------
-- 10 — Kevin, evangelist
------------------------------------------------------------------------------
SET @s = DATEADD(MINUTE, -3, DATEADD(HOUR, -68,  @Now)); SET @c = DATEADD(HOUR, -68,  @Now);
INSERT INTO dbo.SurveyResponses (id, survey_id, status, answers_json, current_question_index, device_type, started_at, completed_at, created_date, updated_date)
VALUES ('a0000000-0000-0000-0000-000000000010', @SurveyId, 'completed',
N'{"q_name":"Kevin","q_nps":10,"q_satisfaction":5,"q_frequency":"Daily","q_features":["Dashboard","Detailed reports","Mobile app","API access","Email notifications","Custom workflows"],"q_dimensions":{"Ease of use":5,"Performance and speed":5,"Customer support":5,"Documentation and help":4,"Value for money":5},"q_priorities":["More features","Better performance","Better support","Improved UI","Lower price"],"q_budget":{"New features":30,"Performance improvements":25,"Better support":15,"Lower prices":10,"UI/UX polish":20},"q_emoji":"😍","q_feedback":"Genuinely the best tool of its kind I''ve used in my 15-year career. The depth of functionality, the polish, the integrations — everything works together. We''ve consolidated three separate tools into just this one and saved both money and headaches. The custom workflow builder alone is worth the price. My team''s productivity is way up since we adopted it."}',
10, 'desktop', @s, @c, @s, @c);

------------------------------------------------------------------------------
-- 11 — Daniel, change fatigue
------------------------------------------------------------------------------
SET @s = DATEADD(MINUTE, -5, DATEADD(HOUR, -76,  @Now)); SET @c = DATEADD(HOUR, -76,  @Now);
INSERT INTO dbo.SurveyResponses (id, survey_id, status, answers_json, current_question_index, device_type, started_at, completed_at, created_date, updated_date)
VALUES ('a0000000-0000-0000-0000-000000000011', @SurveyId, 'completed',
N'{"q_name":"Daniel","q_nps":3,"q_satisfaction":2,"q_frequency":"Weekly","q_features":["Dashboard","Detailed reports"],"q_dimensions":{"Ease of use":2,"Performance and speed":3,"Customer support":3,"Documentation and help":2,"Value for money":3},"q_priorities":["Better support","Improved UI","Better performance","More features","Lower price"],"q_budget":{"New features":10,"Performance improvements":10,"Better support":25,"Lower prices":20,"UI/UX polish":35},"q_emoji":"😡","q_feedback":"Every two months you redesign the navigation and I have to retrain my team. The new sidebar layout from last quarter was actively worse — buried frequently-used items three levels deep. Stop changing things that work. Also, the notification system is broken: I get duplicate emails for the same event and sometimes nothing at all when I really need to know. Please focus on stability instead of redesigns."}',
10, 'desktop', @s, @c, @s, @c);

------------------------------------------------------------------------------
-- 12 — Emily, dark-mode / shortcuts wishlist
------------------------------------------------------------------------------
SET @s = DATEADD(MINUTE, -3, DATEADD(HOUR, -84,  @Now)); SET @c = DATEADD(HOUR, -84,  @Now);
INSERT INTO dbo.SurveyResponses (id, survey_id, status, answers_json, current_question_index, device_type, started_at, completed_at, created_date, updated_date)
VALUES ('a0000000-0000-0000-0000-000000000012', @SurveyId, 'completed',
N'{"q_name":"Emily","q_nps":8,"q_satisfaction":4,"q_frequency":"Daily","q_features":["Dashboard","Detailed reports","Email notifications","Custom workflows"],"q_dimensions":{"Ease of use":4,"Performance and speed":4,"Customer support":5,"Documentation and help":3,"Value for money":4},"q_priorities":["More features","Better performance","Improved UI","Better support","Lower price"],"q_budget":{"New features":30,"Performance improvements":25,"Better support":15,"Lower prices":10,"UI/UX polish":20},"q_emoji":"😊","q_feedback":"I genuinely enjoy using this every day. The dashboard is my homepage now. A few wishes: dark mode (please!), keyboard shortcuts for power users, and the ability to pin frequently-used reports. None of these are dealbreakers, just quality-of-life things. Overall really happy with the product direction over the past year."}',
10, 'desktop', @s, @c, @s, @c);

------------------------------------------------------------------------------
-- 13 — Robert, API + integrations
------------------------------------------------------------------------------
SET @s = DATEADD(MINUTE, -4, DATEADD(HOUR, -92,  @Now)); SET @c = DATEADD(HOUR, -92,  @Now);
INSERT INTO dbo.SurveyResponses (id, survey_id, status, answers_json, current_question_index, device_type, started_at, completed_at, created_date, updated_date)
VALUES ('a0000000-0000-0000-0000-000000000013', @SurveyId, 'completed',
N'{"q_name":"Robert","q_nps":6,"q_satisfaction":3,"q_frequency":"Weekly","q_features":["API access","Email notifications"],"q_dimensions":{"Ease of use":3,"Performance and speed":3,"Customer support":4,"Documentation and help":3,"Value for money":3},"q_priorities":["More features","Better performance","Better support","Improved UI","Lower price"],"q_budget":{"New features":15,"Performance improvements":15,"Better support":20,"Lower prices":10,"UI/UX polish":40},"q_emoji":"😐","q_feedback":"We mostly use this through the API to feed data into our BI tools. The API itself is reasonable but the rate limiting is too aggressive — 100 requests per minute is hard to work with when you''re syncing data across thousands of records. Also, no native Snowflake or BigQuery integrations means we have to roll our own ETL pipeline. Would love to see more first-party integrations with major data platforms."}',
10, 'desktop', @s, @c, @s, @c);

------------------------------------------------------------------------------
-- 14 — Patricia, evangelist
------------------------------------------------------------------------------
SET @s = DATEADD(MINUTE, -3, DATEADD(HOUR, -100, @Now)); SET @c = DATEADD(HOUR, -100, @Now);
INSERT INTO dbo.SurveyResponses (id, survey_id, status, answers_json, current_question_index, device_type, started_at, completed_at, created_date, updated_date)
VALUES ('a0000000-0000-0000-0000-000000000014', @SurveyId, 'completed',
N'{"q_name":"Patricia","q_nps":9,"q_satisfaction":5,"q_frequency":"Daily","q_features":["Dashboard","Detailed reports","Mobile app","Custom workflows"],"q_dimensions":{"Ease of use":5,"Performance and speed":5,"Customer support":5,"Documentation and help":5,"Value for money":4},"q_priorities":["Better performance","More features","Better support","Improved UI","Lower price"],"q_budget":{"New features":25,"Performance improvements":30,"Better support":20,"Lower prices":10,"UI/UX polish":15},"q_emoji":"😍","q_feedback":"I''ve been recommending this product to anyone who''ll listen. It''s transformed how my team handles reporting — what used to take a full Friday now takes 20 minutes. The mobile app got a major upgrade last quarter and is now genuinely useful when I''m traveling. Worth every penny. Keep building, keep improving — you''re on the right track."}',
10, 'mobile', @s, @c, @s, @c);

------------------------------------------------------------------------------
-- 15 — Mark, churn risk
------------------------------------------------------------------------------
SET @s = DATEADD(MINUTE, -5, DATEADD(HOUR, -108, @Now)); SET @c = DATEADD(HOUR, -108, @Now);
INSERT INTO dbo.SurveyResponses (id, survey_id, status, answers_json, current_question_index, device_type, started_at, completed_at, created_date, updated_date)
VALUES ('a0000000-0000-0000-0000-000000000015', @SurveyId, 'completed',
N'{"q_name":"Mark","q_nps":2,"q_satisfaction":2,"q_frequency":"Monthly","q_features":["Dashboard"],"q_dimensions":{"Ease of use":2,"Performance and speed":2,"Customer support":2,"Documentation and help":1,"Value for money":2},"q_priorities":["Better support","Lower price","More features","Better performance","Improved UI"],"q_budget":{"New features":20,"Performance improvements":15,"Better support":30,"Lower prices":25,"UI/UX polish":10},"q_emoji":"😡","q_feedback":"Support response times have been getting worse and worse. My last ticket sat for nine days before getting a generic ''we''re looking into this'' reply. The product is okay but we''re paying premium pricing and getting basic-tier service. We''re actively evaluating alternatives — looking at three competitors now. If something doesn''t change before renewal, we''ll be gone."}',
10, 'desktop', @s, @c, @s, @c);

------------------------------------------------------------------------------
-- 16 — Christina, SSO and permissions
------------------------------------------------------------------------------
SET @s = DATEADD(MINUTE, -3, DATEADD(HOUR, -116, @Now)); SET @c = DATEADD(HOUR, -116, @Now);
INSERT INTO dbo.SurveyResponses (id, survey_id, status, answers_json, current_question_index, device_type, started_at, completed_at, created_date, updated_date)
VALUES ('a0000000-0000-0000-0000-000000000016', @SurveyId, 'completed',
N'{"q_name":"Christina","q_nps":7,"q_satisfaction":4,"q_frequency":"Daily","q_features":["Dashboard","Detailed reports","API access","Custom workflows"],"q_dimensions":{"Ease of use":4,"Performance and speed":4,"Customer support":4,"Documentation and help":3,"Value for money":4},"q_priorities":["More features","Better support","Improved UI","Better performance","Lower price"],"q_budget":{"New features":35,"Performance improvements":10,"Better support":25,"Lower prices":10,"UI/UX polish":20},"q_emoji":"😊","q_feedback":"Great product overall but the permission system is too coarse-grained for our enterprise needs. Right now everyone is either an admin or read-only — there''s no way to grant specific feature access. Single sign-on through SAML would also be huge for our security team. These feel like table stakes for an enterprise tool at your price point. Otherwise, very happy."}',
10, 'desktop', @s, @c, @s, @c);

------------------------------------------------------------------------------
-- 17 — Stephen, mobile-first
------------------------------------------------------------------------------
SET @s = DATEADD(MINUTE, -3, DATEADD(HOUR, -124, @Now)); SET @c = DATEADD(HOUR, -124, @Now);
INSERT INTO dbo.SurveyResponses (id, survey_id, status, answers_json, current_question_index, device_type, started_at, completed_at, created_date, updated_date)
VALUES ('a0000000-0000-0000-0000-000000000017', @SurveyId, 'completed',
N'{"q_name":"Stephen","q_nps":8,"q_satisfaction":4,"q_frequency":"Several times a week","q_features":["Mobile app","Email notifications"],"q_dimensions":{"Ease of use":4,"Performance and speed":4,"Customer support":3,"Documentation and help":3,"Value for money":4},"q_priorities":["Improved UI","More features","Better performance","Better support","Lower price"],"q_budget":{"New features":25,"Performance improvements":10,"Better support":10,"Lower prices":10,"UI/UX polish":45},"q_emoji":"😊","q_feedback":"I almost exclusively use the mobile app since I''m in the field a lot. The recent redesign made it much more usable on phones — bigger touch targets, less cramped layout, better offline mode. Would love to see Apple Watch complications for key metrics and a widget for iOS home screen. Make mobile a first-class citizen and you''ll convert a lot of frustrated users."}',
10, 'mobile', @s, @c, @s, @c);

------------------------------------------------------------------------------
-- 18 — Nicole, reliability
------------------------------------------------------------------------------
SET @s = DATEADD(MINUTE, -3, DATEADD(HOUR, -132, @Now)); SET @c = DATEADD(HOUR, -132, @Now);
INSERT INTO dbo.SurveyResponses (id, survey_id, status, answers_json, current_question_index, device_type, started_at, completed_at, created_date, updated_date)
VALUES ('a0000000-0000-0000-0000-000000000018', @SurveyId, 'completed',
N'{"q_name":"Nicole","q_nps":7,"q_satisfaction":4,"q_frequency":"Daily","q_features":["Dashboard","Detailed reports","API access","Email notifications"],"q_dimensions":{"Ease of use":4,"Performance and speed":3,"Customer support":4,"Documentation and help":4,"Value for money":4},"q_priorities":["Better performance","Better support","More features","Improved UI","Lower price"],"q_budget":{"New features":15,"Performance improvements":40,"Better support":25,"Lower prices":10,"UI/UX polish":10},"q_emoji":"😊","q_feedback":"Reliability has improved significantly over the past year and that''s been the most important thing for us. We rely on this system for daily operations and downtime is expensive. That said, I still see occasional API timeouts and the status page sometimes shows green when things are clearly degraded. Better incident communication would build a lot of trust."}',
10, 'desktop', @s, @c, @s, @c);

------------------------------------------------------------------------------
-- 19 — Brandon, engineering / API quality
------------------------------------------------------------------------------
SET @s = DATEADD(MINUTE, -4, DATEADD(HOUR, -140, @Now)); SET @c = DATEADD(HOUR, -140, @Now);
INSERT INTO dbo.SurveyResponses (id, survey_id, status, answers_json, current_question_index, device_type, started_at, completed_at, created_date, updated_date)
VALUES ('a0000000-0000-0000-0000-000000000019', @SurveyId, 'completed',
N'{"q_name":"Brandon","q_nps":9,"q_satisfaction":5,"q_frequency":"Daily","q_features":["Dashboard","API access","Custom workflows","Email notifications"],"q_dimensions":{"Ease of use":4,"Performance and speed":5,"Customer support":5,"Documentation and help":4,"Value for money":5},"q_priorities":["More features","Better performance","Improved UI","Better support","Lower price"],"q_budget":{"New features":30,"Performance improvements":25,"Better support":10,"Lower prices":15,"UI/UX polish":20},"q_emoji":"😍","q_feedback":"From an engineering perspective this is one of the best-designed B2B tools I''ve used. The API is well-thought-out, webhook support is solid, the data model is clean and intuitive once you understand it. I do wish the OpenAPI spec was more accurate — there are several undocumented response fields and the auth section is confusing for OAuth implementations. Small nitpicks though, this is a high-quality product."}',
10, 'desktop', @s, @c, @s, @c);

------------------------------------------------------------------------------
-- 20 — Olivia, accessibility
------------------------------------------------------------------------------
SET @s = DATEADD(MINUTE, -3, DATEADD(HOUR, -148, @Now)); SET @c = DATEADD(HOUR, -148, @Now);
INSERT INTO dbo.SurveyResponses (id, survey_id, status, answers_json, current_question_index, device_type, started_at, completed_at, created_date, updated_date)
VALUES ('a0000000-0000-0000-0000-000000000020', @SurveyId, 'completed',
N'{"q_name":"Olivia","q_nps":7,"q_satisfaction":4,"q_frequency":"Daily","q_features":["Dashboard","Detailed reports","Email notifications"],"q_dimensions":{"Ease of use":4,"Performance and speed":4,"Customer support":4,"Documentation and help":4,"Value for money":3},"q_priorities":["Improved UI","Better support","More features","Better performance","Lower price"],"q_budget":{"New features":15,"Performance improvements":10,"Better support":20,"Lower prices":10,"UI/UX polish":45},"q_emoji":"😊","q_feedback":"Really good product but the accessibility could be much better. Several of my colleagues use screen readers and they struggle with the navigation. Color contrast is poor in a few key places and the data tables aren''t keyboard-navigable. WCAG 2.1 AA compliance should be a baseline. Otherwise, I''m a happy customer and have been for two years."}',
10, 'desktop', @s, @c, @s, @c);

GO

PRINT '20 demo responses inserted.';
GO
