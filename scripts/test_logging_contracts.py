import unittest

from check_source_contracts import has_interpolated_log


class LoggingContractsTests(unittest.TestCase):
    def test_constant_templates_with_structured_values_are_allowed(self):
        self.assertFalse(has_interpolated_log(
            'logger.LogError(ex, "Operation failed {CorrelationId}", correlationId);'))

    def test_interpolated_message_after_exception_is_rejected(self):
        self.assertTrue(has_interpolated_log(
            'logger.LogError(ex,\n $"Saving {customer.Name} failed");'))

    def test_serilog_interpolated_message_is_rejected(self):
        self.assertTrue(has_interpolated_log(
            'Log.Information($@"Saved {privateValue}");'))

    def test_unrelated_interpolation_after_a_completed_log_is_allowed(self):
        self.assertFalse(has_interpolated_log(
            'logger.LogInformation("Operation finished"); var title = $"Hello {name}";'))
