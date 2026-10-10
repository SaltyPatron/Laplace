require 'yaml'
require 'minitest/autorun'

class SiteWorkflow < Minitest::Test
  def setup
    @workflow = YAML.load_file(File.expand_path('../.github/workflows/laplace.yml', __dir__))
    @jobs = @workflow.fetch('jobs')
  end

  def test_production_requires_staging_and_cloud_start
    assert_equal 'deliver', @jobs.fetch('knowledge-host').fetch('needs')
    assert_equal %w[deliver knowledge-host], @jobs.fetch('production-build').fetch('needs')
    assert_equal 'production-build', @jobs.fetch('production').fetch('needs')
    %w[deliver production-build production].each do |name|
      refute @jobs.fetch(name).fetch('steps').any? { |step| step['continue-on-error'] }
    end
  end

  def test_hosting_migration_precedes_publication_and_readback
    runs = @jobs.fetch('production').fetch('steps').filter_map { |step| step['run'] }
    deploy = runs.index('deploy/linux/site.sh receive migrate_app publish restart')
    smoke = runs.index('deploy/linux/site.sh smoke')
    refute_nil deploy
    refute_nil smoke
    assert_operator deploy, :<, smoke
  end

  def test_lease_release_waits_for_hosting_and_handles_failure
    release = @jobs.fetch('knowledge-host-release')
    assert_includes release.fetch('needs'), 'production'
    assert_includes release.fetch('if'), 'always()'
  end

  def test_bundle_keeps_receipts_and_missing_artifact_fails
    upload = @jobs.fetch('production-build').fetch('steps').find { |step| step.fetch('uses', '').start_with?('actions/upload-artifact@') }
    assert_equal true, upload.fetch('with').fetch('include-hidden-files')
    assert_equal 'error', upload.fetch('with').fetch('if-no-files-found')
  end
end
