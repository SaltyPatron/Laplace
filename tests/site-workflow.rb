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

  def test_delivery_is_automatic_and_maintenance_is_separate
    triggers = @workflow.fetch('on') { @workflow.fetch(true) }
    assert_equal ['main'], triggers.fetch('push').fetch('branches')
    refute triggers.key?('workflow_dispatch')
    refute triggers.key?('pull_request')
    refute @jobs.key?('mainline-qualification')
    groups = %w[deliver production-build production].map do |name|
      concurrency = @jobs.fetch(name).fetch('concurrency')
      assert_equal false, concurrency.fetch('cancel-in-progress')
      concurrency.fetch('group')
    end
    assert_equal groups.uniq, groups
    operator = YAML.load_file(File.expand_path('../.github/workflows/product-operator.yml', __dir__))
    assert (operator['on'] || operator[true]).key?('workflow_dispatch')
  end

  def test_policy_pull_requests_run_on_hosted_runner
    policy = YAML.load_file(File.expand_path('../.github/workflows/ci-contract.yml', __dir__))
    assert (policy['on'] || policy[true]).key?('pull_request')
    assert_equal 'ubuntu-24.04', policy.fetch('jobs').fetch('contract').fetch('runs-on')
    assert_equal 'read', policy.fetch('permissions').fetch('contents')
  end

  def test_active_runtime_changes_are_not_ignored
    triggers = @workflow['on'] || @workflow[true]
    ignored = triggers.fetch('push').fetch('paths-ignore')
    %w[deploy/linux/site.sh app/Laplace.Migrations/Program.cs db/migrations/example.sql engine/core/src/example.c].each do |path|
      refute ignored.any? { |pattern| File.fnmatch?(pattern, path, File::FNM_PATHNAME | File::FNM_DOTMATCH) }, path
    end
  end

  def test_legacy_full_qualification_stays_explicit_until_ported
    audit = YAML.load_file(File.expand_path('../.github/workflows/full-qualification.yml', __dir__))
    triggers = audit['on'] || audit[true]
    assert triggers.key?('workflow_dispatch')
    refute triggers.key?('schedule')
    settings = audit.fetch('jobs').fetch('qualify').fetch('with')
    assert_equal 'release-qualification', settings.fetch('stage')
    assert_equal 'all', settings.fetch('build_components')
    assert_equal 'all', settings.fetch('dev_suites')
    assert_equal false, settings.fetch('skip_if_superseded')
  end

  def test_benchmark_targets_the_selected_host
    workflow = YAML.load_file(File.expand_path('../.github/workflows/benchmark-evidence.yml', __dir__))
    runners = workflow.fetch('jobs').values.map { |job| job.fetch('runs-on') }
    assert_equal [['self-hosted', 'laplace', '${{ inputs.host }}']], runners
    refute workflow.key?('concurrency')
  end
end
