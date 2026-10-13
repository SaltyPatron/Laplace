require 'yaml'
require 'minitest/autorun'
require 'tmpdir'
require 'fileutils'
require 'open3'

class SiteWorkflow < Minitest::Test
  def setup
    @workflow = YAML.load_file(File.expand_path('../.github/workflows/laplace.yml', __dir__))
    @jobs = @workflow.fetch('jobs')
  end

  def test_production_requires_staging_and_cloud_qualification_is_explicit
    assert_equal 'deliver', @jobs.fetch('cloud-host').fetch('needs')
    assert_equal %w[deliver cloud-host], @jobs.fetch('production-build').fetch('needs')
    assert_equal 'deliver', @jobs.fetch('production').fetch('needs')
    assert_includes @jobs.fetch('cloud-host').fetch('if'), 'inputs.qualify_cloud == true'
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

  def test_lease_release_waits_for_cloud_qualification_and_handles_failure
    release = @jobs.fetch('cloud-host-release')
    assert_equal %w[cloud-host production-build], release.fetch('needs')
    assert_includes release.fetch('if'), 'always()'
  end

  def test_bundle_keeps_receipts_and_missing_artifact_fails
    upload = @jobs.fetch('deliver').fetch('steps').find { |step| step.fetch('name', '') == 'Retain the verified production package' }
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

  def test_product_maintenance_uses_the_selected_host_and_installed_lifecycle
    workflow = YAML.load_file(File.expand_path('../.github/workflows/product-operator.yml', __dir__))
    job = workflow.fetch('jobs').fetch('operator')
    assert_equal ['self-hosted', 'laplace', '${{ inputs.target }}'], job.fetch('runs-on')
    refute job.key?('uses'), 'maintenance must not enter the retired source delivery chain'
    assert_equal false, job.fetch('concurrency').fetch('cancel-in-progress')
    step = job.fetch('steps').find { |candidate| candidate['env']&.key?('OPERATION') }
    assert_equal '${{ inputs.operation }}', step.fetch('env').fetch('OPERATION')
    assert_includes step.fetch('run'), 'bash deploy/linux/site.sh "$OPERATION"'
    assert_includes step.fetch('run'), 'set -euo pipefail'
  end

  def test_active_runtime_changes_are_not_ignored
    triggers = @workflow['on'] || @workflow[true]
    ignored = triggers.fetch('push').fetch('paths-ignore')
    %w[deploy/linux/site.sh app/Laplace.Migrations/Program.cs db/migrations/example.sql engine/core/src/example.c].each do |path|
      refute ignored.any? { |pattern| File.fnmatch?(pattern, path, File::FNM_PATHNAME | File::FNM_DOTMATCH) }, path
    end
  end

  def test_full_qualification_stays_explicit_and_uses_the_host_lifecycle
    audit = YAML.load_file(File.expand_path('../.github/workflows/full-qualification.yml', __dir__))
    triggers = audit['on'] || audit[true]
    assert triggers.key?('workflow_dispatch')
    refute triggers.key?('schedule')
    settings = audit.fetch('jobs').fetch('qualify').fetch('with')
    assert_equal 'qualification', settings.fetch('operation')
    internal = YAML.load_file(File.expand_path('../.github/workflows/product-stage.yml', __dir__))
    job = internal.fetch('jobs').fetch('stage')
    assert_equal @jobs.fetch('deliver').fetch('concurrency'), job.fetch('concurrency')
    assert_equal ['self-hosted', 'laplace', 'Laplace', 'staging'], job.fetch('runs-on')
    refute job.fetch('env').keys.any? { |key| key.match?(/SECRET|TOKEN|API_KEY/) }
    step = job.fetch('steps').find { |s| s.fetch('run', '').include?('site.sh') }
    assert_includes step.fetch('run'), 'bash deploy/linux/site.sh "$OPERATION"'
    assert_includes step.fetch('run'), 'set -euo pipefail'
  end

  def test_benchmark_targets_the_selected_host
    workflow = YAML.load_file(File.expand_path('../.github/workflows/benchmark-evidence.yml', __dir__))
    runners = workflow.fetch('jobs').values.map { |job| job.fetch('runs-on') }
    assert_equal [['self-hosted', 'laplace', '${{ inputs.host }}']], runners
    refute workflow.key?('concurrency')
  end

  def test_status_step_propagates_failure_and_requires_a_report
    workflow = YAML.load_file(File.expand_path('../.github/workflows/product-operator.yml', __dir__))
    step = workflow.fetch('jobs').fetch('verify').fetch('steps').find do |item|
      item['name'] == 'Read installed configuration and target connectivity'
    end
    Dir.mktmpdir('status-step-') do |root|
      FileUtils.mkdir_p(File.join(root, 'deploy/linux'))
      script = File.join(root, 'deploy/linux/site.sh')
      ["exit 23\n", "exit 0\n"].each do |body|
        File.write(script, body)
        out, error, status = Open3.capture3('bash', '-e', '-c', step.fetch('run'), chdir: root)
        refute status.success?, "Accepted failed or empty status: #{out} #{error}"
      end
      File.write(script, "printf '%s\\n' '{\"host\":\"fixture\",\"app_dir\":\"/fixture/app\"}'\n")
      out, error, status = Open3.capture3('bash', '-e', '-c', step.fetch('run'), chdir: root)
      assert status.success?, "Rejected valid status: #{out} #{error}"
    end
  end
end
