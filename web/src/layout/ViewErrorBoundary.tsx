import { Component, type ErrorInfo, type ReactNode } from 'react';
import { reportClientFailure } from '../diagnostics/client';
import { Link } from 'react-router-dom';
import { Button, ErrorText, Panel } from '@ui';

interface Props { resetKey: string; children: ReactNode }
interface State { error: Error | null; resetKey: string }

/** A broken view must not remove navigation or strand the whole application. */
export class ViewErrorBoundary extends Component<Props, State> {
  state: State = { error: null, resetKey: this.props.resetKey };
  static getDerivedStateFromError(error: Error) { return { error }; }
  componentDidCatch(error: Error, info: ErrorInfo) {
    reportClientFailure('view.render', error, { stack: `${error.stack ?? ''}\n${info.componentStack ?? ''}` });
  }
  static getDerivedStateFromProps(props: Props, state: State) {
    return props.resetKey !== state.resetKey ? { error: null, resetKey: props.resetKey } : null;
  }
  render() {
    if (!this.state.error) return this.props.children;
    return <Panel title="This view could not be displayed">
      <ErrorText>The rest of Laplace is still available. Retry this view or use the navigation to open another workspace.</ErrorText>
      <details><summary>Error details</summary><pre>{this.state.error.message}</pre></details>
      <Button onClick={() => this.setState({ error: null })}>Retry view</Button>{' '}
      <Link to="/explore">Go to search</Link>
    </Panel>;
  }
}
