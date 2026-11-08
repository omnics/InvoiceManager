# Alerting and cost guardrails. Every name, display name and description carries the
# environment in capitals (e.g. "InvoiceManager-PRODUCTION-failed-runs"), because Azure
# Monitor and budget emails lead with these names - an operator must be able to tell a test
# alert from a production one at a glance.

locals {
  alert_environment_label = upper(var.environment)
  alert_name_prefix       = "InvoiceManager-${local.alert_environment_label}"
}

resource "azurerm_monitor_action_group" "alerts" {
  name                = "${local.alert_name_prefix}-alerts"
  resource_group_name = azurerm_resource_group.invoice_manager.name
  # Max 12 characters; shown as the sender name in some notification channels.
  short_name = var.environment == "production" ? "IM-PROD" : "IM-TEST"

  email_receiver {
    name                    = "operator"
    email_address           = var.alert_email
    use_common_alert_schema = true
  }
}

# Any processing run that failed outright, or completed with failed records or failed record
# generation. Unmatched FreeAgent bills are reported separately ("FreeAgent conflict") and are
# deliberately not treated as failures.
resource "azurerm_monitor_scheduled_query_rules_alert_v2" "failed_runs" {
  name                 = "${local.alert_name_prefix}-failed-runs"
  display_name         = "[${local.alert_environment_label}] InvoiceManager: processing run failed"
  description          = "[${local.alert_environment_label}] An InvoiceManager processing, generation or resync execution failed, or reported failed records. Check the ${var.environment} admin website and Application Insights."
  resource_group_name  = azurerm_resource_group.invoice_manager.name
  location             = azurerm_resource_group.invoice_manager.location
  scopes               = [azurerm_application_insights.main.id]
  severity             = 2
  evaluation_frequency = "PT15M"
  window_duration      = "PT15M"

  criteria {
    query                   = <<-QUERY
      let failedExecutions = requests
        | where name in ("GenerateExpectedRecordsTimer", "GenerateExpectedRecordsHttp", "ResyncInvoiceRecordHttp")
        | where success == false;
      let failedSummaries = traces
        | where message startswith "Due invoice processing run complete" or message startswith "Expected record generation complete"
        | extend failed = toint(extract(@"(\d+) failed", 1, message))
        | where failed > 0;
      union failedExecutions, failedSummaries
    QUERY
    time_aggregation_method = "Count"
    operator                = "GreaterThan"
    threshold               = 0

    failing_periods {
      minimum_failing_periods_to_trigger_alert = 1
      number_of_evaluation_periods             = 1
    }
  }

  action {
    action_groups = [azurerm_monitor_action_group.alerts.id]
  }
}

# Microsoft or FreeAgent authorization that has expired, been revoked, or was never captured -
# every unattended run will keep failing until an administrator re-authorizes on the admin
# website. Matches the token providers' own failure messages plus MSAL/Entra error markers.
resource "azurerm_monitor_scheduled_query_rules_alert_v2" "authorization_failures" {
  name                 = "${local.alert_name_prefix}-authorization-failures"
  display_name         = "[${local.alert_environment_label}] InvoiceManager: re-authorization needed"
  description          = "[${local.alert_environment_label}] InvoiceManager could not obtain a Microsoft or FreeAgent token. Re-authorize on the ${var.environment} admin website's Authorization page."
  resource_group_name  = azurerm_resource_group.invoice_manager.name
  location             = azurerm_resource_group.invoice_manager.location
  scopes               = [azurerm_application_insights.main.id]
  severity             = 1
  evaluation_frequency = "PT15M"
  window_duration      = "PT15M"

  criteria {
    query                   = <<-QUERY
      union traces, exceptions
        | extend text = strcat(tostring(message), " ", tostring(outerMessage), " ", tostring(innermostMessage), " ", tostring(type))
        | where text has_any (
            "FreeAgent token refresh failed",
            "No FreeAgent refresh token is stored",
            "No delegated account is available in the MSAL token cache",
            "MsalUiRequiredException",
            "AADSTS")
    QUERY
    time_aggregation_method = "Count"
    operator                = "GreaterThan"
    threshold               = 0

    failing_periods {
      minimum_failing_periods_to_trigger_alert = 1
      number_of_evaluation_periods             = 1
    }
  }

  action {
    action_groups = [azurerm_monitor_action_group.alerts.id]
  }
}

# The hourly timer has not run for two hours. Only enabled while the timer itself is enabled,
# so a deliberate hold (functions_generation_timer_enabled = false) never raises it.
# Resolves itself once runs resume, so a recovered timer does not leave the alert open. Expect one
# firing just after the timer goes from off to on: the first check looks back over the hold.
resource "azurerm_monitor_scheduled_query_rules_alert_v2" "missing_scheduled_runs" {
  name                    = "${local.alert_name_prefix}-missing-scheduled-runs"
  display_name            = "[${local.alert_environment_label}] InvoiceManager: hourly run missing"
  description             = "[${local.alert_environment_label}] GenerateExpectedRecordsTimer has not executed in the last two hours although it is enabled. Check the ${var.environment} Function App."
  resource_group_name     = azurerm_resource_group.invoice_manager.name
  location                = azurerm_resource_group.invoice_manager.location
  scopes                  = [azurerm_application_insights.main.id]
  severity                = 2
  enabled                 = var.functions_generation_timer_enabled
  auto_mitigation_enabled = true
  evaluation_frequency    = "PT1H"
  window_duration         = "PT2H"

  criteria {
    query                   = <<-QUERY
      requests
        | where name == "GenerateExpectedRecordsTimer"
        | summarize executions = count()
    QUERY
    time_aggregation_method = "Total"
    metric_measure_column   = "executions"
    operator                = "LessThan"
    threshold               = 1

    failing_periods {
      minimum_failing_periods_to_trigger_alert = 1
      number_of_evaluation_periods             = 1
    }
  }

  action {
    action_groups = [azurerm_monitor_action_group.alerts.id]
  }
}

# Budgets must start on the first of a month and cannot move afterwards; pin the start to the
# month this environment's budget was first created.
resource "time_static" "budget_start" {}

resource "azurerm_consumption_budget_resource_group" "monthly" {
  name              = "${local.alert_name_prefix}-monthly-budget"
  resource_group_id = azurerm_resource_group.invoice_manager.id
  amount            = var.monthly_budget_amount
  time_grain        = "Monthly"

  time_period {
    start_date = formatdate("YYYY-MM-01'T'00:00:00Z", time_static.budget_start.rfc3339)
  }

  notification {
    enabled        = true
    threshold      = 80
    operator       = "GreaterThan"
    threshold_type = "Actual"
    contact_emails = [var.alert_email]
  }

  notification {
    enabled        = true
    threshold      = 100
    operator       = "GreaterThan"
    threshold_type = "Forecasted"
    contact_emails = [var.alert_email]
  }
}
