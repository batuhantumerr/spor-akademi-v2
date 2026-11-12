import { Dialog } from '@angular/cdk/dialog';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { DialogFocusRestorer } from './dialog-focus';

@Component({ selector: 'app-test-dialog', template: '<button>inside</button>' })
class TestDialog {}

describe('DialogFocusRestorer (P3-11, DEC-441)', () => {
  let trigger: HTMLButtonElement;

  beforeEach(() => {
    TestBed.configureTestingModule({});
    TestBed.runInInjectionContext(() => TestBed.inject(DialogFocusRestorer).watch());
    trigger = document.createElement('button');
    trigger.textContent = 'open';
    document.body.appendChild(trigger);
  });

  afterEach(() => {
    trigger.remove();
  });

  it('focuses the trigger again once the page enables it after the dialog closed', async () => {
    trigger.focus();
    const ref = TestBed.inject(Dialog).open(TestDialog);
    // The page's re-entry guard disables the trigger while the dialog and the reload after it run.
    trigger.disabled = true;
    ref.close();
    // A browser cannot focus the disabled trigger, so focus ends up on nothing (jsdom would allow it).
    await new Promise((resolve) => setTimeout(resolve, 10));
    (document.activeElement as HTMLElement | null)?.blur();

    trigger.disabled = false;
    await new Promise((resolve) => setTimeout(resolve, 120));
    expect(document.activeElement).toBe(trigger);
  });

  it('leaves focus alone when something else already has it', async () => {
    const other = document.createElement('input');
    document.body.appendChild(other);
    trigger.focus();
    const ref = TestBed.inject(Dialog).open(TestDialog);
    ref.close();
    other.focus();
    await new Promise((resolve) => setTimeout(resolve, 120));
    expect(document.activeElement).toBe(other);
    other.remove();
  });
});
