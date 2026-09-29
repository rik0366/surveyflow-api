-- =============================================================================
-- Inserts a demo customer-experience survey owned by richardlaub@gmail.com.
-- Showcases 10 question types — basic (short_text, multiple_choice, rating,
-- long_text) plus advanced (NPS, Likert grid, rank-order, percent allocation,
-- emoji, checkbox).
--
-- Re-runnable: deletes any prior copy with the same id before inserting.
-- =============================================================================

USE SurveyFlow;
GO

DECLARE @SurveyId CHAR(36) = 'd5f4e7b3-8c2a-4e9d-b1f6-7a3c5e2d4f8a';
DECLARE @Email    NVARCHAR(320) = 'richardlaub@gmail.com';

-- Wipe any prior demo so this script is idempotent.
DELETE FROM dbo.SurveyResponses WHERE survey_id = @SurveyId;
DELETE FROM dbo.SurveyInvites   WHERE survey_id = @SurveyId;
DELETE FROM dbo.Surveys         WHERE id        = @SurveyId;

INSERT INTO dbo.Surveys (id, title, description, status, questions_json, settings_json, created_by)
VALUES (
    @SurveyId,
    N'Customer Experience Survey (Demo)',
    N'A short feedback survey that exercises a mix of SurveyFlow question types — basic and advanced.',
    N'active',
    N'[
      {
        "id": "q_name",
        "type": "short_text",
        "text": "What''s your first name?",
        "description": "We''ll use this to personalize your responses.",
        "required": true
      },
      {
        "id": "q_nps",
        "type": "nps",
        "text": "How likely are you to recommend us to a friend or colleague?",
        "required": true,
        "nps_low_label": "Not at all likely",
        "nps_high_label": "Extremely likely"
      },
      {
        "id": "q_satisfaction",
        "type": "rating",
        "text": "Overall, how satisfied are you with our product?",
        "required": true,
        "rating_max": 5
      },
      {
        "id": "q_frequency",
        "type": "multiple_choice",
        "text": "How often do you use our product?",
        "required": true,
        "options": ["Daily", "Several times a week", "Weekly", "Monthly", "Rarely"]
      },
      {
        "id": "q_features",
        "type": "checkbox",
        "text": "Which features do you regularly use?",
        "description": "Select all that apply.",
        "required": false,
        "options": ["Dashboard", "Detailed reports", "Mobile app", "API access", "Email notifications", "Custom workflows"]
      },
      {
        "id": "q_dimensions",
        "type": "likert",
        "text": "Please rate your experience across these dimensions.",
        "required": true,
        "likert_scale": "satisfaction",
        "likert_rows": ["Ease of use", "Performance and speed", "Customer support", "Documentation and help", "Value for money"]
      },
      {
        "id": "q_priorities",
        "type": "rank_order",
        "text": "Rank these areas in order of importance to you (drag to reorder).",
        "required": true,
        "options": ["More features", "Better performance", "Lower price", "Better support", "Improved UI"]
      },
      {
        "id": "q_budget",
        "type": "percent_allocation",
        "text": "If we had $100 to invest in improvements, how would you allocate it?",
        "description": "Distribute 100 points across the categories below.",
        "required": true,
        "allocation_must_total_100": true,
        "options": ["New features", "Performance improvements", "Better support", "Lower prices", "UI/UX polish"]
      },
      {
        "id": "q_emoji",
        "type": "emoji",
        "text": "Pick the emoji that best captures how using our product makes you feel.",
        "required": false,
        "emoji_options": ["😍", "😊", "😐", "😕", "😡"],
        "emoji_labels": ["Love it", "Happy", "Neutral", "Frustrated", "Angry"],
        "emoji_max_select": 1
      },
      {
        "id": "q_feedback",
        "type": "long_text",
        "text": "Is there anything else you''d like to share about your experience?",
        "description": "Open-ended feedback helps us improve.",
        "required": false
      }
    ]',
    N'{
      "allow_anonymous": true,
      "show_progress_bar": true,
      "welcome_message": "Thanks for taking a few minutes to share your feedback!",
      "completion_message": "Thanks for your responses! Your feedback helps us improve.",
      "ai_enabled": true
    }',
    @Email
);
GO

PRINT 'Demo survey inserted.';
PRINT 'Open it at: https://surveyflow.periscopeiq.com/surveys/d5f4e7b3-8c2a-4e9d-b1f6-7a3c5e2d4f8a/edit';
GO
