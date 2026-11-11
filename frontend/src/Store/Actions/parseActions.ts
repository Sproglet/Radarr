import { Dispatch } from 'redux';
import { batchActions } from 'redux-batched-actions';
import AppState from 'App/State/AppState';
import { createThunk, handleThunks } from 'Store/thunks';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import { set, update } from './baseActions';
import createHandleActions from './Creators/createHandleActions';
import createClearReducer from './Creators/Reducers/createClearReducer';

interface FetchPayload {
  title: string;
  movieEditionId?: number;
}

//
// Variables

export const section = 'parse';
let parseTimeout: number | null = null;
let abortCurrentRequest: (() => void) | null = null;
let requestGeneration = 0;

function cancelPending() {
  requestGeneration += 1;
  if (parseTimeout !== null) {
    clearTimeout(parseTimeout);
    parseTimeout = null;
  }
  abortCurrentRequest?.();
  abortCurrentRequest = null;
}

//
// State

export const defaultState = {
  isFetching: false,
  isPopulated: false,
  error: null,
  item: {},
};

//
// Actions Types

export const FETCH = 'parse/fetch';
export const CLEAR = 'parse/clear';
const RESET = 'parse/reset';

//
// Action Creators

export const fetch = createThunk(FETCH);
export const clear = createThunk(CLEAR);

//
// Action Handlers

export const actionHandlers = handleThunks({
  [FETCH]: function (
    _getState: () => AppState,
    payload: FetchPayload,
    dispatch: Dispatch
  ) {
    cancelPending();
    const generation = requestGeneration;
    dispatch(
      set({ section, isFetching: true, isPopulated: false, error: null })
    );

    parseTimeout = window.setTimeout(async () => {
      parseTimeout = null;
      const { request, abortRequest } = createAjaxRequest({
        url: '/parse',
        data: payload,
      });
      abortCurrentRequest = abortRequest;

      try {
        const data = await request;
        if (generation !== requestGeneration) {
          return;
        }

        dispatch(
          batchActions([
            update({ section, data }),
            set({ section, isFetching: false, isPopulated: true, error: null }),
          ])
        );
      } catch (error) {
        if (generation !== requestGeneration) {
          return;
        }

        dispatch(
          set({
            section,
            item: {},
            isFetching: false,
            isPopulated: false,
            error,
          })
        );
      } finally {
        if (generation === requestGeneration) {
          abortCurrentRequest = null;
        }
      }
    }, 300);
  },
  [CLEAR]: function (
    _getState: () => AppState,
    _payload: never,
    dispatch: Dispatch
  ) {
    cancelPending();
    dispatch({ type: RESET });
  },
});

//
// Reducers

export const reducers = createHandleActions(
  {
    [RESET]: createClearReducer(section, defaultState),
  },
  defaultState,
  section
);
